using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace JackLLM.Security;

public sealed class SecurityEngine {
    private readonly SecurityStateStore _store;
    private readonly bool _development;
    private readonly ConcurrentDictionary<string, PendingChallenge> _challenges = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _grants = new();

    public SecurityEngine(SecurityStateStore store, bool development) {
        _store = store;
        _development = development;
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
                return Response(SecurityStateKind.Cooldown, "The system clock moved backward. Unlocking is suspended for 24 hours.", record.HardwareId, record.CooldownUntilUtc);
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
            return Response(SecurityStateKind.Locked, "Verify Windows Hello, then enter the workstation password.", record.HardwareId);
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
        if (operation == SecurityOperation.BeginUnlock && status.State is not (SecurityStateKind.Unenrolled or SecurityStateKind.Locked or SecurityStateKind.HardwareMismatch or SecurityStateKind.CredentialMissing or SecurityStateKind.CorruptEnrollment))
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
            EnrolledUtc = DateTimeOffset.UtcNow,
            LastObservedUtc = DateTimeOffset.UtcNow
        });
        CryptographicOperations.ZeroMemory(pepper);
        CryptographicOperations.ZeroMemory(verifier);
        CryptographicOperations.ZeroMemory(recoveryVerifier);
        string grant = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _grants[grant] = DateTimeOffset.UtcNow.AddSeconds(30);
        return new SecurityResponse { Success = true, State = SecurityStateKind.Unlocked, Message = "Enrollment complete.", HardwareId = hardwareId, RecoveryKey = recoveryKey, RecoveryBackup = recoveryBackup, UnlockGrant = grant, DevelopmentMode = _development };
    }

    public SecurityResponse Unlock(SecurityRequest request) {
        WorkstationCredentialRecord? record;
        try { record = _store.Load(); } catch { return Response(SecurityStateKind.CorruptEnrollment, "The enrollment cannot be read."); }
        if (record == null) return GetStatus();
        if (record.CooldownUntilUtc > DateTimeOffset.UtcNow) return GetStatus();
        if (!TryConsumeChallenge(request, SecurityOperation.BeginUnlock, out byte[] challenge))
            return Response(SecurityStateKind.Error, "The unlock challenge expired or was already used.");

        bool helloValid = VerifyHelloSignature(record.HelloPublicKey, challenge, request.Signature);
        byte[] pepper = Convert.FromBase64String(record.Pepper);
        bool passwordValid;
        try { passwordValid = PasswordSecurity.Verify(request.Password ?? "", Convert.FromBase64String(record.Salt), pepper, Convert.FromBase64String(record.PasswordVerifier)); }
        finally { CryptographicOperations.ZeroMemory(pepper); }
        if (!helloValid || !passwordValid) {
            record.FailedAttempts++;
            TimeSpan delay = PasswordSecurity.GetCooldown(record.FailedAttempts);
            record.CooldownUntilUtc = delay > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(delay) : null;
            _store.Save(record);
            SecurityResponse failed = Response(record.CooldownUntilUtc.HasValue ? SecurityStateKind.Cooldown : SecurityStateKind.Locked,
                "Windows Hello or the workstation password was not accepted.", record.HardwareId, record.CooldownUntilUtc);
            return failed;
        }

        record.FailedAttempts = 0;
        record.CooldownUntilUtc = null;
        _store.Save(record);
        string grant = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _grants[grant] = DateTimeOffset.UtcNow.AddSeconds(30);
        return new SecurityResponse { Success = true, State = SecurityStateKind.Unlocked, Message = "Workstation unlocked.", HardwareId = record.HardwareId, UnlockGrant = grant, DevelopmentMode = _development };
    }

    public SecurityResponse ChangePassword(SecurityRequest request) {
        WorkstationCredentialRecord? record = TryLoadPrimary();
        if (record == null) return GetStatus();
        if (!TryConsumeChallenge(request, SecurityOperation.BeginUnlock, out byte[] challenge))
            return Response(SecurityStateKind.Error, "The password-change challenge expired or was already used.");
        if (!VerifyHelloSignature(record.HelloPublicKey, challenge, request.Signature))
            return RegisterFailure(record);
        byte[] pepper = Convert.FromBase64String(record.Pepper);
        try {
            if (!PasswordSecurity.Verify(request.Password ?? "", Convert.FromBase64String(record.Salt), pepper, Convert.FromBase64String(record.PasswordVerifier)))
                return RegisterFailure(record!);
            string? validation = PasswordSecurity.Validate(request.NewPassword);
            if (validation != null) return Response(SecurityStateKind.Locked, validation, record.HardwareId);
            byte[] salt = PasswordSecurity.CreateSalt();
            byte[] verifier = PasswordSecurity.Derive(request.NewPassword!, salt, pepper);
            record.Salt = Convert.ToBase64String(salt);
            record.PasswordVerifier = Convert.ToBase64String(verifier);
            record.FailedAttempts = 0;
            record.CooldownUntilUtc = null;
            _store.Save(record);
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
                return RegisterFailure(record);
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
            record.FailedAttempts = 0;
            record.CooldownUntilUtc = null;
            _store.Save(record);
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
                return RegisterFailure(record);
            record.HelloPublicKey = request.PublicKey;
            record.HelloAttestation = request.Attestation;
            record.HardwareId = HardwareIdentity.Compute(request.PublicKey);
            record.FailedAttempts = 0;
            record.CooldownUntilUtc = null;
            _store.Save(record);
            SecurityResponse rebound = Response(SecurityStateKind.Locked, "Hardware binding updated.", record.HardwareId);
            rebound.Success = true;
            return rebound;
        } finally { CryptographicOperations.ZeroMemory(pepper); }
    }

    public bool ConsumeGrant(string? grant) {
        if (string.IsNullOrWhiteSpace(grant) || !_grants.TryRemove(grant, out DateTimeOffset expires)) return false;
        return expires >= DateTimeOffset.UtcNow;
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

    private SecurityResponse RegisterFailure(WorkstationCredentialRecord record) {
        record.FailedAttempts++;
        record.LastObservedUtc = DateTimeOffset.UtcNow;
        TimeSpan delay = PasswordSecurity.GetCooldown(record.FailedAttempts);
        record.CooldownUntilUtc = delay > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(delay) : null;
        _store.Save(record);
        return Response(record.CooldownUntilUtc.HasValue ? SecurityStateKind.Cooldown : SecurityStateKind.Locked,
            "Authentication was not accepted.", record.HardwareId, record.CooldownUntilUtc);
    }

    private static bool VerifyHelloSignature(string? publicKey, byte[] challenge, string? signature) {
        try {
            using CngKey key = CngKey.Import(Convert.FromBase64String(publicKey ?? ""), CngKeyBlobFormat.GenericPublicBlob);
            using var rsa = new RSACng(key);
            return rsa.VerifyData(challenge, Convert.FromBase64String(signature ?? ""), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        } catch { return false; }
    }

    private SecurityResponse Response(SecurityStateKind state, string message, string? hardwareId = null, DateTimeOffset? cooldown = null) =>
        new() { Success = false, State = state, Message = message, HardwareId = hardwareId, CooldownUntilUtc = cooldown, DevelopmentMode = _development };

    private sealed record PendingChallenge(SecurityOperation Operation, byte[] Nonce, DateTimeOffset ExpiresUtc);
}
