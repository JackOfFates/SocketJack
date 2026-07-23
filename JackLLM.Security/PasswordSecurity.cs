using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace JackLLM.Security;

public static class PasswordSecurity {
    public const int MinimumLength = 16;
    public const int MaximumLength = 128;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int MemoryKilobytes = 64 * 1024;
    public const int Iterations = 3;
    public const int Parallelism = 4;

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase) {
        "password", "password1", "password123", "password1234", "qwerty", "qwerty123",
        "letmein", "welcome", "admin", "administrator", "iloveyou", "changeme",
        "1234567890123456", "abcdefghijklmnop", "socketjack", "jackllm"
    };

    public static string? Validate(string? password) {
        if (password == null || password.Length < MinimumLength)
            return $"Use at least {MinimumLength} characters.";
        if (password.Length > MaximumLength)
            return $"Use no more than {MaximumLength} characters.";

        string normalized = password.Trim();
        if (CommonPasswords.Contains(normalized) || CommonPasswords.Any(item => normalized.Contains(item, StringComparison.OrdinalIgnoreCase)))
            return "Choose a password that does not contain a common password or product name.";
        if (normalized.Distinct().Count() < 6)
            return "Use a less repetitive password.";
        if (HasLongSequence(normalized))
            return "Avoid long alphabetical, numeric, or keyboard sequences.";

        int classes = (normalized.Any(char.IsLower) ? 1 : 0) +
                      (normalized.Any(char.IsUpper) ? 1 : 0) +
                      (normalized.Any(char.IsDigit) ? 1 : 0) +
                      (normalized.Any(ch => !char.IsLetterOrDigit(ch)) ? 1 : 0);
        int words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (classes < 3 && !(normalized.Length >= 24 && words >= 4))
            return "Use three character types, or a passphrase of at least four words and 24 characters.";
        return null;
    }

    public static byte[] CreateSalt() => RandomNumberGenerator.GetBytes(SaltBytes);
    public static byte[] CreatePepper() => RandomNumberGenerator.GetBytes(32);

    public static byte[] Derive(string password, byte[] salt, byte[] pepper) {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try {
            using var argon = new Argon2id(passwordBytes) {
                Salt = salt,
                KnownSecret = pepper,
                DegreeOfParallelism = Parallelism,
                Iterations = Iterations,
                MemorySize = MemoryKilobytes
            };
            return argon.GetBytes(HashBytes);
        } finally {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static bool Verify(string password, byte[] salt, byte[] pepper, byte[] expected) {
        byte[] actual = Derive(password, salt, pepper);
        try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    public static string CreateRecoveryKey() {
        byte[] bytes = RandomNumberGenerator.GetBytes(24);
        string encoded = Convert.ToHexString(bytes);
        return string.Join('-', Enumerable.Range(0, 8).Select(i => encoded.Substring(i * 6, 6)));
    }

    public static string NormalizeRecoveryKey(string? value) =>
        new((value ?? "").Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    public static TimeSpan GetCooldown(int failedAttempts) => failedAttempts switch {
        >= 10 => TimeSpan.FromHours(24),
        >= 7 => TimeSpan.FromMinutes(15),
        >= 5 => TimeSpan.FromMinutes(1),
        >= 3 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.Zero
    };

    private static bool HasLongSequence(string value) {
        const string sequences = "0123456789|9876543210|abcdefghijklmnopqrstuvwxyz|zyxwvutsrqponmlkjihgfedcba|qwertyuiop|poiuytrewq";
        string lower = value.ToLowerInvariant();
        return sequences.Split('|').Any(sequence =>
            Enumerable.Range(0, Math.Max(0, sequence.Length - 5)).Any(i => lower.Contains(sequence.Substring(i, 6), StringComparison.Ordinal)));
    }
}
