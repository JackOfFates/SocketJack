using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace JackLLM.Security;

public sealed class SecurityEngine {
    private readonly SecurityStateStore _store;
    private readonly bool _development;
    private readonly INetworkBindingProvider _networkBindingProvider;
    private readonly ConcurrentDictionary<string, PendingChallenge> _challenges = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _grants = new();

    public SecurityEngine(SecurityStateStore store, bool development, INetworkBindingProvider? networkBindingProvider = null) {
        _store = store;
        _development = development;
        _networkBindingProvider = networkBindingProvider ?? new NetworkBindingProvider();
    }

    public SecurityResponse GetStatus() {
        if (!_store.StateExists) {
            return _store.IsMarkedEnrolled()
                ? Response(SecurityStateKind.CredentialMissing, "Enrollment data is missing. Use recovery; setup will not be reopened.")
                : Response(SecurityStateKind.Unenrolled, "Set a workstation password to finish security enrollment.");
        }
        try {
            WorkstationCredentialRecord record = _store.Load() ?? throw new InvalidDataException();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (record.LastObservedUtc > now.AddMinutes(5)) {
                record.CooldownUntilUtc = now.AddHours(24);
                _store.Save(record);
                _store.DeleteRememberedDevice();
                return Response(SecurityStateKind.Cooldown, "The system clock moved backward. Unlocking is suspended for 24 hours.", record.HardwareId, record.CooldownUntilUtc);
            }
            if (string.IsNullOrWhiteSpace(record.CredentialGeneration)) {
                record.CredentialGeneration = CreateCredentialGeneration();
                _store.Save(record);
                _store.DeleteRememberedDevice();
            }
            if (record.LastObservedUtc == null || now - record.LastObservedUtc > TimeSpan.FromMinutes(1)) {
                record.LastObservedUtc = now;
                _store.Save(record);
            }
            string currentHardwareId = HardwareIdentity.Compute(record.HelloPublicKey);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(currentHardwareId), Convert.FromHexString(record.HardwareId)))
                return Response(SecurityStateKind.HardwareMismatch, "This enrollment is bound to different hardware.", record.HardwareId);
            if (record.CooldownUntilUtc > DateTimeOffset.UtcNow)
                return Response(SecurityStateKind.Cooldown, "Too many failed attempts.", record.HardwareId, record.CooldownUntilUtc);
            return Response(SecurityStateKind.Locked,
                record.WindowsHelloOnly
                    ? "Verify Windows Hello to open JackLLM Workstation."
                    : "Verify Windows Hello to switch this workstation to password-free sign-in.",
                record.HardwareId);
        } catch (CryptographicException) {
            return Response(SecurityStateKind.HardwareMismatch, "The protected enrollment cannot be opened on this Windows installation.");
        } catch {
            return Response(SecurityStateKind.CorruptEnrollment, "The workstation enrollment is corrupt. Use recovery.");
        }
    }

    public SecurityResponse Begin(SecurityOperation operation) {
        SecurityResponse status = GetStatus();
        if (operation == SecurityOperation.BeginEnroll && status.State != SecurityStateKind.Unenrolled)
            return status;
        if (operation == SecurityOperation.BeginUnlock && status.State is not (SecurityStateKind.Unenrolled or SecurityStateKind.Locked or SecurityStateKind.Cooldown or SecurityStateKind.HardwareMismatch or SecurityStateKind.CredentialMissing or SecurityStateKind.CorruptEnrollment))
            return status;
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        string id = Guid.NewGuid().ToString("N");
        _challenges[id] = new PendingChallenge(operation, nonce, DateTimeOffset.UtcNow.AddMinutes(2));
        return new SecurityResponse {
            Success = true,
            State = status.State,
            ChallengeId = id,
            Challenge = Convert.ToBase64String(nonce),
            HardwareId = status.HardwareId,
            DevelopmentMode = _development
        };
    }

    public SecurityResponse Enroll(SecurityRequest request) {
        if (!TryConsumeChallenge(request, SecurityOperation.BeginEnroll, out byte[] challenge))
            return Response(SecurityStateKind.Error, "The enrollment challenge expired or was already used.");
        string? validation = PasswordSecurity.Validate(request.Password);
        if (validation != null) return Response(SecurityStateKind.Unenrolled, validation);
        if (string.IsNullOrWhiteSpace(request.PublicKey) || string.IsNullOrWhiteSpace(request.Signature) ||
            string.IsNullOrWhiteSpace(request.Attestation))
            return Response(SecurityStateKind.UnsupportedHardware, "A TPM-attested Windows Hello key is required.");
        if (!VerifyHelloSignature(request.PublicKey, challenge, request.Signature))
            return Response(SecurityStateKind.UnsupportedHardware, "Windows Hello challenge verification failed.");

        byte[] pepper = PasswordSecurity.CreatePepper();
        byte[] salt = PasswordSecurity.CreateSalt();
        byte[] recoverySalt = PasswordSecurity.CreateSalt();
        string recoveryKey = PasswordSecurity.CreateRecoveryKey();
        string recoveryBackup = PortableRecovery.Create(recoveryKey);
        byte[] verifier = PasswordSecurity.Derive(request.Password!, salt, pepper);
        byte[] recoveryVerifier = PasswordSecurity.Derive(PasswordSecurity.NormalizeRecoveryKey(recoveryKey), recoverySalt, pepper);
        string hardwareId = HardwareIdentity.Compute(request.PublicKey);
        _store.Save(new WorkstationCredentialRecord {
            HardwareId = hardwareId,
            HelloPublicKey = request.PublicKey,
            HelloAttestation = request.Attestation,
            Salt = Convert.ToBase64String(salt),
            PasswordVerifier = Convert.ToBase64String(verifier),
            RecoverySalt = Convert.ToBase64String(recoverySalt),
            RecoveryVerifier = Convert.ToBase64String(recoveryVerifier),
            Pepper = Convert.ToBase64String(pepper),
            WindowsHelloOnly = request.UseWindowsHelloOnly,
            EnrolledUtc = DateTimeOffset.UtcNow,
            LastObservedUtc = DateTimeOffset.UtcNow
            ,CredentialGeneration = CreateCredentialGeneration()
        });
        _store.DeleteRememberedDevice();
        CryptographicOperations.ZeroMemory(pepper);
        CryptographicOperations.ZeroMemory(verifier);
        CryptographicOperations.ZeroMemory(recoveryVerifier);
        string grant = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset grantExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10);
        // Enrollment pauses while the user chooses a location and saves the generated recovery file.
        // Keep this grant short-lived, but allow enough time for that required human workflow.
        _grants[grant] = grantExpiresUtc;
        var response = new SecurityResponse { Success = true, State = SecurityStateKind.Unlocked, Message = "Enrollment complete.", HardwareId = hardwareId, RecoveryKey = recoveryKey, RecoveryBackup = recoveryBackup, UnlockGrant = grant, UnlockGrantExpiresUtc = grantExpiresUtc, DevelopmentMode = _development };
        if (request.RememberDevice)
            AddRememberedDevice(response, _store.Load()!);
        return response;
    }

    public SecurityResponse Unlock(SecurityRequest request) {
        WorkstationCredentialRecord? record;
        try { record = _store.Load(); } catch { return Response(SecurityStateKind.CorruptEnrollment, "The enrollment cannot be read."); }
        if (record == null) return GetStatus();
        if (!TryConsumeChallenge(request, SecurityOperation.BeginUnlock, out byte[] challenge))
            return Response(SecurityStateKind.Error, "The unlock challenge expired or was already used.");

        bool helloValid = VerifyHelloSignature(record.HelloPublicKey, challenge, request.Signature);
        if (!helloValid)
            return RejectHello(record);

        if (record.WindowsHelloOnly || request.UseWindowsHelloOnly) {
            bool migratingToHelloOnly = !record.WindowsHelloOnly;
            record.WindowsHelloOnly = true;
            record.FailedAttempts = 0;
            record.CooldownUntilUtc = null;
            record.LastObservedUtc = DateTimeOffset.UtcNow;
            if (migratingToHelloOnly) {
                record.CredentialGeneration = CreateCredentialGeneration();
                _store.DeleteRememberedDevice();
            }
            _store.Save(record);
            return CreateUnlockResponse(request, record,
                migratingToHelloOnly
                    ? "Windows Hello verified. Password sign-in was removed and the stale lockout was cleared."
                    : "Windows Hello verified. Workstation unlocked.");
        }

        if (record.CooldownUntilUtc > DateTimeOffset.UtcNow) return GetStatus();

        byte[] pepper = Convert.FromBase64String(record.Pepper);
        bool passwordValid;
        try { passwordValid = PasswordSecurity.Verify(request.Password ?? "", Convert.FromBase64String(record.Salt), pepper, Convert.FromBase64String(record.PasswordVerifier)); }
        finally { CryptographicOperations.ZeroMemory(pepper); }
        if (!passwordValid)
            return RegisterPasswordFailure(record,
                "Windows Hello verified, but the workstation password was not accepted.");

        record.FailedAttempts = 0;
        record.CooldownUntilUtc = null;
        _store.Save(record);
        return CreateUnlockResponse(request, record, "Workstation unlocked.");
    }

    public SecurityResponse ChangePassword(SecurityRequest request) {
        WorkstationCredentialRecord? record = TryLoadPrimary();
        if (record == null) return GetStatus();
        if (!TryConsumeChallenge(request, SecurityOperation.BeginUnlock, out byte[] challenge))
            return Response(SecurityStateKind.Error, "The password-change challenge expired or was already used.");
        if (!VerifyHelloSignature(record.HelloPublicKey, challenge, request.Signature))
            return RejectHello(record);
        byte[] pepper = Convert.FromBase64String(record.Pepper);
        try {
            if (!PasswordSecurity.Verify(request.Password ?? "", Convert.FromBase64String(record.Salt), pepper, Convert.FromBase64String(record.PasswordVerifier)))
                return RegisterPasswordFailure(record!,
                    "Windows Hello verified, but the current workstation password was not accepted.");
            string? validation = PasswordSecurity.Validate(request.NewPassword);
            if (validation != null) return Response(SecurityStateKind.Locked, validation, record.HardwareId);
            byte[] salt = PasswordSecurity.CreateSalt();
            byte[] verifier = PasswordSecurity.Derive(request.NewPassword!, salt, pepper);
            record.Salt = Convert.ToBase64String(salt);
            record.PasswordVerifier = Convert.ToBase64String(verifier);
            record.CredentialGeneration = CreateCredentialGeneration();
            record.FailedAttempts = 0;
            record.CooldownUntilUtc = null;
            _store.Save(record);
            _store.DeleteRememberedDevice();
            CryptographicOperations.ZeroMemory(verifier);
            SecurityResponse changed = Response(SecurityStateKind.Locked, "The workstation password was changed.", record.HardwareId);
            changed.Success = true;
            return changed;
        } finally { CryptographicOperations.ZeroMemory(pepper); }
    }

    public SecurityResponse Recover(SecurityRequest request) {
        WorkstationCredentialRecord? record = TryLoadRecovery();
        if (!TryConsumeChallenge(request, SecurityOperation.BeginUnlock, out byte[] challenge))
            return Response(SecurityStateKind.Error, "The recovery challenge expired or was already used.");
        string? validation = PasswordSecurity.Validate(request.NewPassword);
        if (validation != null) return Response(SecurityStateKind.Locked, validation, record?.HardwareId);
        if (string.IsNullOrWhiteSpace(request.PublicKey) || string.IsNullOrWhiteSpace(request.Attestation) ||
            !VerifyHelloSignature(request.PublicKey, challenge, request.Signature))
            return Response(SecurityStateKind.UnsupportedHardware, "A new TPM-attested Windows Hello key is required for recovery.");
        bool portableRecovery = record == null && PortableRecovery.Validate(request.RecoveryKey, request.RecoveryBackup);
        if (record == null && !portableRecovery)
            return Response(SecurityStateKind.CorruptEnrollment, "Select a valid JackLLM recovery-key file from the original enrollment.");
        byte[] pepper = record == null ? PasswordSecurity.CreatePepper() : Convert.FromBase64String(record.Pepper);
        try {
            if (!portableRecovery && !PasswordSecurity.Verify(PasswordSecurity.NormalizeRecoveryKey(request.RecoveryKey), Convert.FromBase64String(record!.RecoverySalt), pepper, Convert.FromBase64String(record.RecoveryVerifier)))
                return RegisterPasswordFailure(record);
            string recoveryKey = request.RecoveryKey ?? "";
            byte[] recoverySalt = record == null ? PasswordSecurity.CreateSalt() : Convert.FromBase64String(record.RecoverySalt);
            byte[] recoveryVerifier = record == null
                ? PasswordSecurity.Derive(PasswordSecurity.NormalizeRecoveryKey(recoveryKey), recoverySalt, pepper)
                : Convert.FromBase64String(record.RecoveryVerifier);
            byte[] salt = PasswordSecurity.CreateSalt();
            byte[] verifier = PasswordSecurity.Derive(request.NewPassword!, salt, pepper);
            record ??= new WorkstationCredentialRecord {
                Pepper = Convert.ToBase64String(pepper), RecoverySalt = Convert.ToBase64String(recoverySalt),
                RecoveryVerifier = Convert.ToBase64String(recoveryVerifier), EnrolledUtc = DateTimeOffset.UtcNow
            };
            record.Salt = Convert.ToBase64String(salt);
            record.PasswordVerifier = Convert.ToBase64String(verifier);
            record.HelloPublicKey = request.PublicKey;
            record.HelloAttestation = request.Attestation;
            record.HardwareId = HardwareIdentity.Compute(request.PublicKey);
            record.CredentialGeneration = CreateCredentialGeneration();
            record.FailedAttempts = 0;
            record.CooldownUntilUtc = null;
            _store.Save(record);
            _store.DeleteRememberedDevice();
            CryptographicOperations.ZeroMemory(verifier);
            CryptographicOperations.ZeroMemory(recoveryVerifier);
            SecurityResponse recovered = Response(SecurityStateKind.Locked, "Recovery complete. Unlock with the new password.", record.HardwareId);
            recovered.Success = true;
            recovered.RecoveryKey = recoveryKey;
            recovered.RecoveryBackup = request.RecoveryBackup;
            return recovered;
        } finally { CryptographicOperations.ZeroMemory(pepper); }
    }

    public SecurityResponse RebindHardware(SecurityRequest request) {
        WorkstationCredentialRecord? record = TryLoadRecovery();
        if (record == null) return Response(SecurityStateKind.CorruptEnrollment, "Recovery data is unavailable.");
        if (!TryConsumeChallenge(request, SecurityOperation.BeginUnlock, out byte[] challenge))
            return Response(SecurityStateKind.Error, "The hardware-rebind challenge expired or was already used.");
        if (string.IsNullOrWhiteSpace(request.PublicKey) || string.IsNullOrWhiteSpace(request.Attestation) ||
            !VerifyHelloSignature(request.PublicKey, challenge, request.Signature))
            return Response(SecurityStateKind.UnsupportedHardware, "A new TPM-attested Windows Hello key is required.");
        byte[] pepper = Convert.FromBase64String(record.Pepper);
        try {
            if (!PasswordSecurity.Verify(PasswordSecurity.NormalizeRecoveryKey(request.RecoveryKey), Convert.FromBase64String(record.RecoverySalt), pepper, Convert.FromBase64String(record.RecoveryVerifier)))
                return RegisterPasswordFailure(record);
            record.HelloPublicKey = request.PublicKey;
            record.HelloAttestation = request.Attestation;
            record.HardwareId = HardwareIdentity.Compute(request.PublicKey);
            record.CredentialGeneration = CreateCredentialGeneration();
            record.FailedAttempts = 0;
            record.CooldownUntilUtc = null;
            _store.Save(record);
            _store.DeleteRememberedDevice();
            SecurityResponse rebound = Response(SecurityStateKind.Locked, "Hardware binding updated.", record.HardwareId);
            rebound.Success = true;
            return rebound;
        } finally { CryptographicOperations.ZeroMemory(pepper); }
    }

    public bool ConsumeGrant(string? grant) {
        if (string.IsNullOrWhiteSpace(grant) || !_grants.TryRemove(grant, out DateTimeOffset expires)) return false;
        return expires >= DateTimeOffset.UtcNow;
    }

    public SecurityResponse RememberedUnlock(string? token) {
        WorkstationCredentialRecord? credential = TryLoadPrimary();
        RememberedDeviceRecord? remembered;
        try { remembered = _store.LoadRememberedDevice(); }
        catch {
            _store.DeleteRememberedDevice();
            return Response(SecurityStateKind.Locked, "The remembered-device token could not be read. Sign in again.", credential?.HardwareId);
        }
        if (credential == null || remembered == null || string.IsNullOrWhiteSpace(token))
            return Response(SecurityStateKind.Locked, "No remembered-device login is available.", credential?.HardwareId);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (remembered.LastObservedUtc > now.AddMinutes(5))
            return InvalidateRemembered("The system clock moved backward. Sign in again.", credential.HardwareId);
        if (remembered.ExpiresUtc <= now)
            return InvalidateRemembered("The remembered-device login expired after 30 days. Sign in again.", credential.HardwareId);

        byte[] suppliedDigest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        byte[] expectedDigest;
        try { expectedDigest = Convert.FromBase64String(remembered.TokenDigest); }
        catch { return InvalidateRemembered("The remembered-device token is corrupt. Sign in again.", credential.HardwareId); }
        bool tokenValid = expectedDigest.Length == suppliedDigest.Length &&
                          CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
        CryptographicOperations.ZeroMemory(suppliedDigest);
        CryptographicOperations.ZeroMemory(expectedDigest);
        if (!tokenValid)
            return InvalidateRemembered("The remembered-device token was not accepted. Sign in again.", credential.HardwareId);

        string currentHardwareId = HardwareIdentity.Compute(credential.HelloPublicKey);
        if (!FixedHexEquals(currentHardwareId, remembered.HardwareId) ||
            !FixedHexEquals(currentHardwareId, credential.HardwareId))
            return InvalidateRemembered("The workstation hardware changed. Sign in again.", credential.HardwareId);
        if (!FixedTextEquals(credential.CredentialGeneration, remembered.CredentialGeneration))
            return InvalidateRemembered("The workstation password or security enrollment changed. Sign in again.", credential.HardwareId);

        NetworkBindingResult network = _networkBindingProvider.GetCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!FixedTextEquals(network.LocalIpBinding, remembered.LocalIpBinding))
            return InvalidateRemembered("The local network IP changed. Sign in again.", credential.HardwareId);
        if (network.PublicIpVerified) {
            if (!FixedTextEquals(network.PublicIp, remembered.PublicIp))
                return InvalidateRemembered("The public IP changed. Sign in again.", credential.HardwareId);
            remembered.LastPublicIpVerificationUtc = now;
        } else if (now - remembered.LastPublicIpVerificationUtc > TimeSpan.FromHours(1)) {
            return InvalidateRemembered("The public IP could not be verified and the one-hour cache expired. Sign in again.", credential.HardwareId);
        }

        remembered.LastObservedUtc = now;
        _store.SaveRememberedDevice(remembered);
        string grant = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset grantExpiresUtc = now.AddSeconds(30);
        _grants[grant] = grantExpiresUtc;
        return new SecurityResponse {
            Success = true,
            State = SecurityStateKind.Unlocked,
            Message = $"Remembered device verified through {remembered.ExpiresUtc.LocalDateTime:g}.",
            HardwareId = credential.HardwareId,
            UnlockGrant = grant,
            UnlockGrantExpiresUtc = grantExpiresUtc,
            RememberedDeviceExpiresUtc = remembered.ExpiresUtc,
            DevelopmentMode = _development
        };
    }

    private bool TryConsumeChallenge(SecurityRequest request, SecurityOperation expected, out byte[] nonce) {
        nonce = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(request.ChallengeId) || !_challenges.TryRemove(request.ChallengeId, out PendingChallenge? pending)) return false;
        if (pending.Operation != expected || pending.ExpiresUtc < DateTimeOffset.UtcNow) return false;
        nonce = pending.Nonce;
        return true;
    }

    private WorkstationCredentialRecord? TryLoadPrimary() { try { return _store.Load(); } catch { return null; } }
    private WorkstationCredentialRecord? TryLoadRecovery() { try { return _store.LoadRecovery() ?? _store.Load(); } catch { return null; } }

    private SecurityResponse RejectHello(WorkstationCredentialRecord record) =>
        Response(SecurityStateKind.Locked,
            "Windows Hello did not match the enrolled workstation key. The password was not evaluated; use recovery if this continues.",
            record.HardwareId);

    private SecurityResponse CreateUnlockResponse(SecurityRequest request, WorkstationCredentialRecord record, string message) {
        string grant = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset grantExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        _grants[grant] = grantExpiresUtc;
        var response = new SecurityResponse {
            Success = true,
            State = SecurityStateKind.Unlocked,
            Message = message,
            HardwareId = record.HardwareId,
            UnlockGrant = grant,
            UnlockGrantExpiresUtc = grantExpiresUtc,
            DevelopmentMode = _development
        };
        if (request.RememberDevice)
            AddRememberedDevice(response, record);
        return response;
    }

    private SecurityResponse RegisterPasswordFailure(WorkstationCredentialRecord record,
        string message = "Authentication was not accepted.") {
        record.FailedAttempts++;
        record.LastObservedUtc = DateTimeOffset.UtcNow;
        TimeSpan delay = PasswordSecurity.GetCooldown(record.FailedAttempts);
        record.CooldownUntilUtc = delay > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(delay) : null;
        _store.Save(record);
        return Response(record.CooldownUntilUtc.HasValue ? SecurityStateKind.Cooldown : SecurityStateKind.Locked,
            message, record.HardwareId, record.CooldownUntilUtc);
    }

    private void AddRememberedDevice(SecurityResponse response, WorkstationCredentialRecord credential) {
        NetworkBindingResult network = _networkBindingProvider.GetCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!network.PublicIpVerified || string.IsNullOrWhiteSpace(network.PublicIp) || string.IsNullOrWhiteSpace(network.LocalIpBinding)) {
            response.Message += " Remember this device was not enabled because both public and local IP addresses could not be verified.";
            return;
        }
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expires = now.AddDays(30);
        _store.SaveRememberedDevice(new RememberedDeviceRecord {
            TokenDigest = Convert.ToBase64String(digest),
            ExpiresUtc = expires,
            HardwareId = credential.HardwareId,
            CredentialGeneration = credential.CredentialGeneration,
            PublicIp = network.PublicIp,
            LocalIpBinding = network.LocalIpBinding,
            LastPublicIpVerificationUtc = now,
            LastObservedUtc = now
        });
        CryptographicOperations.ZeroMemory(digest);
        response.RememberedDeviceToken = token;
        response.RememberedDeviceExpiresUtc = expires;
        response.Message += $" This device is remembered through {expires.LocalDateTime:g}.";
    }

    private SecurityResponse InvalidateRemembered(string message, string? hardwareId) {
        _store.DeleteRememberedDevice();
        return Response(SecurityStateKind.Locked, message, hardwareId);
    }

    private static string CreateCredentialGeneration() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static bool FixedHexEquals(string? left, string? right) {
        try {
            byte[] leftBytes = Convert.FromHexString(left ?? "");
            byte[] rightBytes = Convert.FromHexString(right ?? "");
            return leftBytes.Length == rightBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        } catch { return false; }
    }

    private static bool FixedTextEquals(string? left, string? right) {
        byte[] leftBytes = System.Text.Encoding.UTF8.GetBytes(left ?? "");
        byte[] rightBytes = System.Text.Encoding.UTF8.GetBytes(right ?? "");
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool VerifyHelloSignature(string? publicKey, byte[] challenge, string? signature) {
        return WindowsHelloCryptography.VerifySignature(publicKey, challenge, signature);
    }

    private SecurityResponse Response(SecurityStateKind state, string message, string? hardwareId = null, DateTimeOffset? cooldown = null) =>
        new() { Success = false, State = state, Message = message, HardwareId = hardwareId, CooldownUntilUtc = cooldown, DevelopmentMode = _development };

    private sealed record PendingChallenge(SecurityOperation Operation, byte[] Nonce, DateTimeOffset ExpiresUtc);
}
