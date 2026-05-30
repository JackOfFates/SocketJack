using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocketJack.Net {

    public static class DirectoryHashMetadata {
        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public static void MapDirectoryHashMetadata(this HttpServer server, string routePrefix, string localDirectory, string publicBaseUrl = null, Func<string, bool> includeRelativePath = null) {
            if (server == null)
                throw new ArgumentNullException(nameof(server));
            if (string.IsNullOrWhiteSpace(routePrefix))
                throw new ArgumentException("Route prefix is required.", nameof(routePrefix));
            if (string.IsNullOrWhiteSpace(localDirectory))
                throw new ArgumentException("Local directory is required.", nameof(localDirectory));

            string normalizedPrefix = NormalizeRoutePrefix(routePrefix);
            string metaRoute = normalizedPrefix == "/" ? "/meta" : normalizedPrefix + "/meta";
            string root = Path.GetFullPath(localDirectory);
            string baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? normalizedPrefix.TrimEnd('/') + "/" : publicBaseUrl;

            server.Map("GET", metaRoute, (connection, request, cancellationToken) => {
                request.Context.Response.Headers["Cache-Control"] = "no-store";
                var manifest = BuildManifest(root, baseUrl, includeRelativePath);
                return JsonSerializer.Serialize(manifest, JsonOptions);
            });
            server.Map("OPTIONS", metaRoute, (connection, request, cancellationToken) => {
                request.Context.StatusCodeNumber = 204;
                request.Context.ReasonPhrase = "No Content";
                return "";
            });
        }

        public static DirectoryHashManifest BuildManifest(string localDirectory, string publicBaseUrl = null, Func<string, bool> includeRelativePath = null) {
            string root = Path.GetFullPath(localDirectory ?? "");
            var manifest = new DirectoryHashManifest {
                Available = Directory.Exists(root),
                Root = root,
                BaseUrl = EnsureTrailingSlash(publicBaseUrl),
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            if (!manifest.Available) {
                manifest.Error = "Directory not found.";
                return manifest;
            }

            foreach (string filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                FileInfo info;
                try {
                    info = new FileInfo(filePath);
                    if (!info.Exists)
                        continue;
                } catch {
                    continue;
                }

                string relativePath = Path.GetRelativePath(root, filePath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;
                if (includeRelativePath != null && !includeRelativePath(relativePath))
                    continue;

                DirectoryHashFile hashFile;
                try {
                    hashFile = HashFile(filePath);
                } catch {
                    continue;
                }

                hashFile.Path = relativePath;
                hashFile.Length = info.Length;
                hashFile.LastWriteUtc = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
                hashFile.Url = string.IsNullOrWhiteSpace(manifest.BaseUrl) ? "" : CombineUrl(manifest.BaseUrl, relativePath);
                manifest.Files.Add(hashFile);
            }

            manifest.Files = manifest.Files
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            manifest.Count = manifest.Files.Count;
            return manifest;
        }

        public static DirectoryHashFile HashFile(string filePath) {
            using FileStream stream = File.OpenRead(filePath);
            string md5;
            using (MD5 md5Algorithm = MD5.Create())
                md5 = ToLowerHex(md5Algorithm.ComputeHash(stream));
            stream.Position = 0;
            string sha256;
            using (SHA256 sha256Algorithm = SHA256.Create())
                sha256 = ToLowerHex(sha256Algorithm.ComputeHash(stream));
            return new DirectoryHashFile {
                Md5 = md5,
                Sha256 = sha256,
                Hash = sha256,
                HashAlgorithm = "SHA256"
            };
        }

        private static string ToLowerHex(byte[] bytes) {
            if (bytes == null || bytes.Length == 0)
                return "";
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string NormalizeRoutePrefix(string routePrefix) {
            string value = (routePrefix ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value))
                return "/";
            if (!value.StartsWith("/", StringComparison.Ordinal))
                value = "/" + value;
            value = value.TrimEnd('/');
            return string.IsNullOrWhiteSpace(value) ? "/" : value;
        }

        private static string EnsureTrailingSlash(string value) {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            value = value.Trim();
            return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
        }

        private static string CombineUrl(string baseUrl, string relativePath) {
            return EnsureTrailingSlash(baseUrl) + (relativePath ?? "").TrimStart('/').Replace("\\", "/");
        }
    }

    public sealed class DirectoryHashManifest {
        public bool Available { get; set; }
        public string Root { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public int Count { get; set; }
        public string Error { get; set; } = "";
        public List<DirectoryHashFile> Files { get; set; } = new List<DirectoryHashFile>();
    }

    public sealed class DirectoryHashFile {
        public string Path { get; set; } = "";
        public string Md5 { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Hash { get; set; } = "";
        public string HashAlgorithm { get; set; } = "SHA256";
        public long Length { get; set; }
        public string LastWriteUtc { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
