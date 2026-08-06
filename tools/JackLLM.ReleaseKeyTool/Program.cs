using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length >= 3 && args[0].Equals("--sign", StringComparison.OrdinalIgnoreCase)) {
    string publishRoot = Path.GetFullPath(args[1]);
    string keyPath = Path.GetFullPath(args[2]);
    using ECDsa releaseSigner = ECDsa.Create();
    releaseSigner.ImportFromPem(File.ReadAllText(keyPath));
    var files = Directory.EnumerateFiles(publishRoot, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetFileName(path).Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase) &&
                       !Path.GetFileName(path).Equals("release-manifest.sig", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path => new {
            path = Path.GetRelativePath(publishRoot, path).Replace('\\', '/'),
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            length = new FileInfo(path).Length
        }).ToArray();
    byte[] manifest = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(new {
        version = 1,
        generatedUtc = DateTimeOffset.UtcNow.ToString("O"),
        files
    }, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllBytes(Path.Combine(publishRoot, "release-manifest.json"), manifest);
    File.WriteAllText(Path.Combine(publishRoot, "release-manifest.sig"),
        Convert.ToBase64String(releaseSigner.SignData(manifest, HashAlgorithmName.SHA256)), new UTF8Encoding(false));
    Console.WriteLine(Convert.ToBase64String(releaseSigner.ExportSubjectPublicKeyInfo()));
    return;
}

string destination = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "JackLLM", "Signing", "manifest-private.pem");
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
File.WriteAllText(destination, signer.ExportECPrivateKeyPem(), new UTF8Encoding(false));
Console.WriteLine(Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
