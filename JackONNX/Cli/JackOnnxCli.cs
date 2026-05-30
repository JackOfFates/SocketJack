using System.Text.Json;
using JackONNX;
using JackONNX.Cuda;
using JackONNX.DirectML;
using JackONNX.Runtime;

namespace JackONNX.Cli;

public static class JackOnnxCli
{
    public static async Task<int> Main(string[] args)
    {
        args ??= Array.Empty<string>();
        var command = args.Length == 0 ? "help" : args[0].Trim().ToLowerInvariant();
        var options = BuildOptions(args.Skip(1));
        var runtime = JackOnnxRuntimeEngine.Create(options, CreateProviders());

        switch (command)
        {
            case "status":
                await WriteStatusAsync(runtime);
                return 0;

            case "devices":
                WriteJson(new { devices = await runtime.ListDevicesAsync() });
                return 0;

            case "providers":
                WriteJson(new { providers = await runtime.CheckProviderCompatibilityAsync() });
                return 0;

            case "models":
                WriteJson(new { models = await runtime.ListModelsAsync() });
                return 0;

            case "validate":
                return await ValidateAsync(options);

            default:
                WriteHelp();
                return command == "help" || command is "-h" or "--help" ? 0 : 2;
        }
    }

static JackOnnxOptions BuildOptions(IEnumerable<string> args)
{
    var options = new JackOnnxOptions();
    foreach (string arg in args)
    {
        if (arg.StartsWith("--models=", StringComparison.OrdinalIgnoreCase))
            options.ModelCachePath = Path.GetFullPath(arg["--models=".Length..].Trim('"'));
        else if (arg.StartsWith("--manifest=", StringComparison.OrdinalIgnoreCase))
            options.ModelManifestPaths.Add(Path.GetFullPath(arg["--manifest=".Length..].Trim('"')));
        else if (arg.StartsWith("--artifacts=", StringComparison.OrdinalIgnoreCase))
            options.ArtifactRoot = Path.GetFullPath(arg["--artifacts=".Length..].Trim('"'));
    }

    return options;
}

static IEnumerable<IJackOnnxExecutionProvider> CreateProviders()
{
    yield return new CudaExecutionProvider();
    yield return new DirectMlExecutionProvider();
    yield return new OnnxRuntimeCpuExecutionProvider();
}

static async Task WriteStatusAsync(JackOnnxRuntimeEngine runtime)
{
    var devices = await runtime.ListDevicesAsync();
    var providers = await runtime.CheckProviderCompatibilityAsync();
    var models = await runtime.ListModelsAsync();
    var jobs = await runtime.ListJobsAsync();
    WriteJson(new
    {
        service = "JackONNX",
        status = "ready",
        devices,
        providers,
        models,
        jobs
    });
}

static async Task<int> ValidateAsync(JackOnnxOptions options)
{
    var results = new List<object>();
    var paths = options.ModelManifestPaths.Count > 0
        ? options.ModelManifestPaths
        : Directory.Exists(options.ModelCachePath)
            ? Directory.EnumerateFiles(options.ModelCachePath, "*.jackonnx.json", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(options.ModelCachePath, "manifest.json", SearchOption.AllDirectories)).ToList()
            : [];

    bool ok = true;
    foreach (string path in paths)
    {
        try
        {
            var manifest = await JackOnnxModelManifest.LoadAsync(path);
            var errors = ValidateManifest(manifest);
            ok &= errors.Count == 0;
            results.Add(new
            {
                path,
                valid = errors.Count == 0,
                manifest.Id,
                manifest.Name,
                manifest.Type,
                errors
            });
        }
        catch (Exception ex)
        {
            ok = false;
            results.Add(new
            {
                path,
                valid = false,
                errors = new[] { ex.Message }
            });
        }
    }

    WriteJson(new
    {
        valid = ok,
        manifest_count = paths.Count,
        manifests = results
    });
    return ok ? 0 : 1;
}

static List<string> ValidateManifest(JackOnnxModelManifest manifest)
{
    var errors = new List<string>();
    if (string.IsNullOrWhiteSpace(manifest.Id))
        errors.Add("id is required.");
    if (string.IsNullOrWhiteSpace(manifest.Name))
        errors.Add("name is required.");
    if (string.IsNullOrWhiteSpace(manifest.Type))
        errors.Add("type is required.");
    if (!string.Equals(manifest.Format, "onnx", StringComparison.OrdinalIgnoreCase))
        errors.Add("format must be onnx.");
    if (manifest.Components.Count == 0)
        errors.Add("at least one component path is required.");

    return errors;
}

static void WriteJson<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value, JackOnnxModelManifest.CreateJsonOptions()));
}

static void WriteHelp()
{
    Console.WriteLine(
        """
        JackONNX CLI

        Usage:
          jackonnx status [--models=PATH] [--manifest=PATH]
          jackonnx devices
          jackonnx providers
          jackonnx models [--models=PATH] [--manifest=PATH]
          jackonnx validate [--models=PATH] [--manifest=PATH]

        Notes:
          JackONNX consumes local manifest/model files only.
        """);
}
}
