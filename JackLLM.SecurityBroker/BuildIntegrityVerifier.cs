using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;

namespace JackLLM.SecurityBroker;

public sealed record BuildIntegrityResult(bool Success, string Message);

public sealed class BuildIntegrityVerifier {
    public BuildIntegrityResult Verify(uint processId, bool development) {
        string? path;
        try { path = Process.GetProcessById(checked((int)processId)).MainModule?.FileName; }
        catch (Exception ex) { return new(false, "Cannot inspect the client executable: " + ex.Message); }
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetFileName(path), "JackLLM.exe", StringComparison.OrdinalIgnoreCase))
            return new(false, "Only JackLLM.exe may request workstation unlocks.");
        if (development)
            return new(true, "Development broker uses isolated credentials.");

        using RegistryKey? config = Registry.LocalMachine.OpenSubKey(@"Software\SocketJack\JackLLM\Security");
        string expectedPublisher = NormalizeHash(config?.GetValue("PublisherCertificateSha256") as string);
        string manifestPublicKey = config?.GetValue("ManifestPublicKeyBase64") as string ?? "";
        if (expectedPublisher.Length != 64 || string.IsNullOrWhiteSpace(manifestPublicKey))
            return new(false, "Official signing is not configured. Install a signed JackLLM package with publisher and manifest keys.");
        if (!AuthenticodeTrust.IsTrusted(path)) return new(false, "The JackLLM executable has no valid Authenticode signature.");
        try {
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            string actualPublisher = NormalizeHash(signer.GetCertHashString(HashAlgorithmName.SHA256));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualPublisher), Convert.FromHexString(expectedPublisher)))
                return new(false, "The JackLLM executable was signed by an untrusted publisher.");
        } catch (Exception ex) { return new(false, "Cannot validate the JackLLM publisher: " + ex.Message); }

        string folder = Path.GetDirectoryName(path) ?? "";
        string manifestPath = Path.Combine(folder, "release-manifest.json");
        string signaturePath = Path.Combine(folder, "release-manifest.sig");
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath)) return new(false, "The signed release manifest is missing.");
        try {
            byte[] manifest = File.ReadAllBytes(manifestPath);
            byte[] signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim());
            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(manifestPublicKey), out _);
            if (!verifier.VerifyData(manifest, signature, HashAlgorithmName.SHA256)) return new(false, "The release manifest signature is invalid.");
            using JsonDocument document = JsonDocument.Parse(manifest);
            JsonElement files = document.RootElement.GetProperty("files");
            string relative = Path.GetFileName(path);
            JsonElement? entry = files.EnumerateArray().FirstOrDefault(item =>
                string.Equals(item.GetProperty("path").GetString(), relative, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return new(false, "JackLLM.exe is not listed in the signed release manifest.");
            string expectedHash = NormalizeHash(entry.Value.GetProperty("sha256").GetString());
            string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (expectedHash.Length != 64 || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(expectedHash)))
                return new(false, "JackLLM.exe does not match the signed release manifest.");
            return new(true, "Official build verified.");
        } catch (Exception ex) { return new(false, "Release manifest verification failed: " + ex.Message); }
    }

    private static string NormalizeHash(string? value) => new((value ?? "").Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
}

internal static class AuthenticodeTrust {
    private static readonly Guid Action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    public static bool IsTrusted(string path) {
        var file = new WinTrustFileInfo(path);
        IntPtr filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(file));
        Marshal.StructureToPtr(file, filePointer, false);
        try {
            var data = new WinTrustData(filePointer);
            return WinVerifyTrust(IntPtr.Zero, Action, ref data) == 0;
        } finally { Marshal.FreeCoTaskMem(filePointer); }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
        public WinTrustFileInfo(string path) { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(); pcwszFilePath = path; hFile = IntPtr.Zero; pgKnownSubject = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public WinTrustData(IntPtr file) { cbStruct = (uint)Marshal.SizeOf<WinTrustData>(); pPolicyCallbackData = IntPtr.Zero; pSIPClientData = IntPtr.Zero; dwUIChoice = 2; fdwRevocationChecks = 0; dwUnionChoice = 1; pFile = file; dwStateAction = 0; hWVTStateData = IntPtr.Zero; pwszURLReference = IntPtr.Zero; dwProvFlags = 0x1000; dwUIContext = 0; }
    }
}
