using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace JackONNX.Video;

public sealed class JackOnnxVideoPresentationConverter
{
    public const string PresentationMediaType = "video/mp4";
    public const string PresentationEncoding = "ffmpeg:h264+aac:yuv420p:faststart";

    public async Task<JackOnnxVideoPresentationConversionResult> TryConvertAsync(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        sourcePath = string.IsNullOrWhiteSpace(sourcePath) ? "" : Path.GetFullPath(sourcePath);
        outputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? "" : Path.GetFullPath(outputDirectory);
        if (sourcePath.Length == 0 || !File.Exists(sourcePath))
            return JackOnnxVideoPresentationConversionResult.Failed("Source video was not found.");
        if (outputDirectory.Length == 0)
            outputDirectory = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;

        string ffmpegPath = ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            return JackOnnxVideoPresentationConversionResult.Failed("FFmpeg was not found. Set JACKONNX_FFMPEG, FFMPEG_PATH, or install C:\\FFMPEG\\ffmpeg.exe.");

        Directory.CreateDirectory(outputDirectory);
        string outputPath = BuildPresentationPath(sourcePath, outputDirectory);
        if (File.Exists(outputPath) && File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(sourcePath) && IsPlayableMp4Candidate(outputPath))
        {
            return JackOnnxVideoPresentationConversionResult.Completed(
                outputPath,
                PresentationMediaType,
                PresentationEncoding,
                "Existing browser-playable presentation video is current.");
        }
        if (File.Exists(outputPath) && !IsPlayableMp4Candidate(outputPath))
            TryDeleteFile(outputPath);

        string tempPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(outputPath) + ".tmp-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a?");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("libx264");
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("yuv420p");
            startInfo.ArgumentList.Add("-preset");
            startInfo.ArgumentList.Add("veryfast");
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("+faststart");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("128k");
            startInfo.ArgumentList.Add(tempPath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return JackOnnxVideoPresentationConversionResult.Failed("FFmpeg did not start.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return JackOnnxVideoPresentationConversionResult.Failed("FFmpeg exited with code " + process.ExitCode.ToString() + ". " + FirstNonEmpty(stderr, stdout));
            if (!File.Exists(tempPath) || !IsPlayableMp4Candidate(tempPath))
                return JackOnnxVideoPresentationConversionResult.Failed("FFmpeg did not create a playable presentation video.");

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);
            return JackOnnxVideoPresentationConversionResult.Completed(
                outputPath,
                PresentationMediaType,
                PresentationEncoding,
                "Browser-playable presentation video prepared.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return JackOnnxVideoPresentationConversionResult.Failed("FFmpeg presentation conversion failed: " + ex.Message);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public static string ResolveFfmpegPath()
    {
        foreach (string? candidate in new string?[]
        {
            Environment.GetEnvironmentVariable("JACKONNX_FFMPEG"),
            Environment.GetEnvironmentVariable("SOCKETJACK_FFMPEG"),
            Environment.GetEnvironmentVariable("FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("FFMPEG"),
            OperatingSystem.IsWindows() ? @"C:\FFMPEG\ffmpeg.exe" : "",
            OperatingSystem.IsWindows() ? @"C:\FFMPEG\FFMPEG.exe" : "",
            "ffmpeg"
        })
        {
            string value = (candidate ?? "").Trim();
            if (value.Length == 0)
                continue;
            if (Path.IsPathRooted(value))
            {
                if (File.Exists(value))
                    return value;
                continue;
            }
            string resolved = ResolveExecutableOnPath(value);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        return "";
    }

    private static string ResolveExecutableOnPath(string fileName)
    {
        fileName = (fileName ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(fileName))
            return "";
        string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (string.IsNullOrWhiteSpace(pathValue))
            return "";

        string[] names = Path.HasExtension(fileName) || !OperatingSystem.IsWindows()
            ? new[] { fileName }
            : new[] { fileName, fileName + ".exe" };
        foreach (string directory in pathValue.Split(Path.PathSeparator))
        {
            string folder = (directory ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            foreach (string name in names)
            {
                try
                {
                    string candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                }
            }
        }

        return "";
    }

    private static string BuildPresentationPath(string sourcePath, string outputDirectory)
    {
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(name))
            name = "video";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath)))).Substring(0, 10).ToLowerInvariant();
        return Path.Combine(outputDirectory, SanitizeFileName(name) + "." + hash + ".presentation.mp4");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (char ch in value ?? "")
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        string result = builder.ToString().Trim();
        return result.Length == 0 ? "video" : result;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static bool IsPlayableMp4Candidate(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            var info = new FileInfo(path);
            if (info.Length < 1024)
                return false;

            byte[] header = new byte[Math.Min(64, (int)info.Length)];
            using var stream = File.OpenRead(path);
            int read = stream.Read(header, 0, header.Length);
            return ContainsAsciiMarker(header, read, "ftyp");
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsAsciiMarker(byte[] data, int length, string marker)
    {
        if (data == null || string.IsNullOrEmpty(marker) || length < marker.Length)
            return false;

        int limit = Math.Min(data.Length, length);
        for (int i = 0; i <= limit - marker.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length; j++)
            {
                if (data[i + j] != (byte)marker[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}

public sealed class JackOnnxVideoPresentationConversionResult
{
    public bool Success { get; set; }
    public string OutputPath { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Encoding { get; set; } = "";
    public string Message { get; set; } = "";

    public static JackOnnxVideoPresentationConversionResult Completed(string outputPath, string mediaType, string encoding, string message) =>
        new()
        {
            Success = true,
            OutputPath = outputPath,
            MediaType = mediaType,
            Encoding = encoding,
            Message = message
        };

    public static JackOnnxVideoPresentationConversionResult Failed(string message) =>
        new()
        {
            Success = false,
            Message = message
        };
}

public sealed class VideoPresentationRequest
{
    public string ArtifactId { get; set; } = "";
}
