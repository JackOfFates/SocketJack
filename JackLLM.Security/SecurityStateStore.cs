using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;

namespace JackLLM.Security;

public sealed class WorkstationCredentialRecord {
    public int Version { get; set; } = 1;
    public string HardwareId { get; set; } = "";
    public string HelloPublicKey { get; set; } = "";
    public string HelloAttestation { get; set; } = "";
    public string Salt { get; set; } = "";
    public string PasswordVerifier { get; set; } = "";
    public string RecoverySalt { get; set; } = "";
    public string RecoveryVerifier { get; set; } = "";
    public string Pepper { get; set; } = "";
    public int FailedAttempts { get; set; }
    public DateTimeOffset? CooldownUntilUtc { get; set; }
    public DateTimeOffset? LastObservedUtc { get; set; }
    public DateTimeOffset EnrolledUtc { get; set; }
}

public sealed class SecurityStateStore {
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SocketJack.JackLLM.Workstation.Security.v1");
    private readonly bool _development;
    private readonly string _statePath;
    private readonly string _recoveryPath;
    private readonly string? _registryPathOverride;

    public SecurityStateStore(bool development, string? root = null, string? registryPathOverride = null) {
        _development = development;
        _registryPathOverride = registryPathOverride;
        string baseRoot = root ?? (development
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JackLLM", "Security", "Development")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JackLLM", "Security"));
        _statePath = Path.Combine(baseRoot, "workstation-access.bin");
        _recoveryPath = Path.Combine(baseRoot, "workstation-recovery.bin");
    }

    public bool StateExists => File.Exists(_statePath);

    public bool IsMarkedEnrolled() {
        using RegistryKey? key = OpenRegistryKey(false);
        return string.Equals(key?.GetValue("EnrollmentState") as string, "Enrolled", StringComparison.OrdinalIgnoreCase);
    }

    public WorkstationCredentialRecord? Load() {
        if (!File.Exists(_statePath)) return null;
        byte[] protectedBytes = File.ReadAllBytes(_statePath);
        byte[] plain = ProtectedData.Unprotect(protectedBytes, Entropy,
            _development ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine);
        try { return JsonSerializer.Deserialize<WorkstationCredentialRecord>(plain, SecurityProtocol.Json); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public WorkstationCredentialRecord? LoadRecovery() => LoadPath(_recoveryPath);

    public void Save(WorkstationCredentialRecord record) {
        string? directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory)) EnsureDirectory(directory);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(record, SecurityProtocol.Json);
        byte[] protectedBytes = ProtectedData.Protect(plain, Entropy,
            _development ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine);
        CryptographicOperations.ZeroMemory(plain);
        string temporary = _statePath + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, _statePath, true);
        string recoveryTemporary = _recoveryPath + ".tmp";
        File.WriteAllBytes(recoveryTemporary, protectedBytes);
        File.Move(recoveryTemporary, _recoveryPath, true);
        using RegistryKey key = OpenRegistryKey(true)!;
        key.SetValue("EnrollmentState", "Enrolled", RegistryValueKind.String);
    }

    private void EnsureDirectory(string directory) {
        Directory.CreateDirectory(directory);
        if (_development) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private WorkstationCredentialRecord? LoadPath(string path) {
        if (!File.Exists(path)) return null;
        byte[] protectedBytes = File.ReadAllBytes(path);
        byte[] plain = ProtectedData.Unprotect(protectedBytes, Entropy,
            _development ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine);
        try { return JsonSerializer.Deserialize<WorkstationCredentialRecord>(plain, SecurityProtocol.Json); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private RegistryKey? OpenRegistryKey(bool writable) {
        RegistryKey root = _development ? Registry.CurrentUser : Registry.LocalMachine;
        string path = _registryPathOverride ?? (_development
            ? @"Software\SocketJack\JackLLM\Security\Development"
            : @"Software\SocketJack\JackLLM\Security");
        return writable ? root.CreateSubKey(path, true) : root.OpenSubKey(path, false);
    }
}
