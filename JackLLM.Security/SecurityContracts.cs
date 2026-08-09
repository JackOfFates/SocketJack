using System.Text.Json;
using System.Text.Json.Serialization;

namespace JackLLM.Security;

public static class SecurityProtocol {
    public const int Version = 4;
    public const int BrokerCompatibility = 5;
    public const string OfficialPipeName = "JackLLM.Workstation.Security.v1";
    public const string DevelopmentPipeName = "JackLLM.Workstation.Security.Development.v1";
    public static readonly JsonSerializerOptions Json = new() {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public enum SecurityOperation {
    Status,
    RestartBroker,
    BeginEnroll,
    Enroll,
    BeginUnlock,
    CompleteUnlock,
    RememberedUnlock,
    ChangePassword,
    Recover,
    RebindHardware
}

public enum SecurityStateKind {
    Unenrolled,
    Locked,
    Cooldown,
    HardwareMismatch,
    CredentialMissing,
    CorruptEnrollment,
    UnsupportedHardware,
    IntegrityFailure,
    Unlocked,
    Error
}

public sealed class SecurityRequest {
    public int Version { get; set; } = SecurityProtocol.Version;
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public SecurityOperation Operation { get; set; }
    public string? ChallengeId { get; set; }
    public string? Password { get; set; }
    public string? NewPassword { get; set; }
    public string? RecoveryKey { get; set; }
    public string? PublicKey { get; set; }
    public string? Signature { get; set; }
    public string? Attestation { get; set; }
    public string? RecoveryBackup { get; set; }
    public string? UnlockGrant { get; set; }
    public bool RememberDevice { get; set; }
    public bool UseWindowsHelloOnly { get; set; }
    public string? RememberedDeviceToken { get; set; }
}

public sealed class SecurityResponse {
    public int Version { get; set; } = SecurityProtocol.Version;
    public bool Success { get; set; }
    public SecurityStateKind State { get; set; }
    public string Message { get; set; } = "";
    public string? ChallengeId { get; set; }
    public string? Challenge { get; set; }
    public string? HardwareId { get; set; }
    public string? RecoveryKey { get; set; }
    public string? RecoveryBackup { get; set; }
    public string? UnlockGrant { get; set; }
    public DateTimeOffset? UnlockGrantExpiresUtc { get; set; }
    public string? RememberedDeviceToken { get; set; }
    public DateTimeOffset? RememberedDeviceExpiresUtc { get; set; }
    public DateTimeOffset? CooldownUntilUtc { get; set; }
    public bool DevelopmentMode { get; set; }
    public int BrokerCompatibility { get; set; }
    public int BrokerProcessId { get; set; }
}

public sealed class PortableRecoveryFile {
    public string Format { get; set; } = "JackLLM Workstation Recovery Key";
    public int Version { get; set; } = 1;
    public string RecoveryKey { get; set; } = "";
    public string RecoveryBackup { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
