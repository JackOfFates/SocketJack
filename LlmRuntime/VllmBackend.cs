using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LlmRuntime;

public sealed class VllmBackend : ILlmBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const uint LongContextCompatibilityThreshold = 65536;
    private const uint LongContextMaxBatchedTokens = 32768;
    private const double LongContextGpuMemoryUtilization = 0.66d;
    private const double LongContextCpuOffloadGb = 20d;
    private static readonly EnumerationOptions DirectorySizeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };
    private const int ProcessLogLineLimit = 40;
    private readonly LlmRuntimeOptions _options;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly LlmBackendLifetimeGate _lifetime = new(nameof(VllmBackend));
    private readonly object _processLogLock = new();
    private readonly Queue<string> _processLogLines = new();
    private Process? _process;
    private Uri _baseUri;
    private string _servedModelName;
    private bool _promptPipelineReady;
    private string _promptPipelineStatus = "cold";
    private string _promptPipelineDetail = "";
    private DateTimeOffset? _promptPipelineReadyAtUtc;
    private double _promptPipelineWarmupSeconds;

    public VllmBackend(string instanceId, string modelPath, LlmLoadConfig loadConfig, LlmRuntimeOptions options)
    {
        InstanceId = instanceId;
        ModelPath = modelPath;
        LoadConfig = loadConfig;
        _options = options ?? new LlmRuntimeOptions();
        _baseUri = NormalizeBaseUri(_options.VllmBaseUrl);
        _servedModelName = ResolveModelDirectory(modelPath);
    }

    public string InstanceId { get; }

    public string ModelPath { get; }

    public LlmLoadConfig LoadConfig { get; }

    public bool IsPromptPipelineReady => _promptPipelineReady;

    public string PromptPipelineStatus => _promptPipelineStatus;

    public string PromptPipelineDetail => _promptPipelineDetail;

    public DateTimeOffset? PromptPipelineReadyAtUtc => _promptPipelineReadyAtUtc;

    public double PromptPipelineWarmupSeconds => _promptPipelineWarmupSeconds;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter();
        _promptPipelineReady = false;
        _promptPipelineReadyAtUtc = null;
        _promptPipelineStatus = "loading";
        _promptPipelineDetail = "Checking vLLM server.";

        if (await ProbeAsync(cancellationToken).ConfigureAwait(false))
        {
            MarkReady("Connected to an existing vLLM server.");
            return;
        }

        string pythonPath = ResolvePythonPath(_options.VllmPythonPath);
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            throw new LlmRuntimeException(
                "vLLM backend requires Python with vLLM installed. Set JACKLLM_VLLM_PYTHON or LLMRUNTIME_VLLM_PYTHON to the vLLM environment python.",
                "backend_error",
                "vllm_python_missing");
        }

        _promptPipelineDetail = "Starting vLLM for " + Path.GetFileName(_servedModelName) + ".";
        _process = StartVllmProcess(pythonPath);
        await WaitUntilReadyAsync(_options.VllmStartupTimeout, cancellationToken).ConfigureAwait(false);
        MarkReady("vLLM started and answered health checks.");
    }

    public async Task WarmPromptPipelineAsync(CancellationToken cancellationToken = default)
    {
        if (_promptPipelineReady)
            return;

        var stopwatch = Stopwatch.StartNew();
        _promptPipelineStatus = "warming";
        _promptPipelineDetail = "Checking vLLM readiness.";
        await WaitUntilReadyAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        _promptPipelineWarmupSeconds = stopwatch.Elapsed.TotalSeconds;
        MarkReady("vLLM is ready.");
    }

    public async Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        JsonElement root = await SendChatRequestAsync(request, stream: false, cancellationToken).ConfigureAwait(false);
        string content = "";
        string finishReason = "stop";
        if (root.TryGetProperty("choices", out JsonElement choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            JsonElement first = choices[0];
            if (first.TryGetProperty("message", out JsonElement message))
                content = ReadString(message, "content");
            finishReason = ReadString(first, "finish_reason");
        }

        stopwatch.Stop();
        return new LlmChatResult
        {
            Model = InstanceId,
            Content = content,
            FinishReason = string.IsNullOrWhiteSpace(finishReason) ? "stop" : finishReason,
            Metrics = LlmInferenceMetrics.FromText(request, content, stopwatch.Elapsed)
        };
    }

    public async IAsyncEnumerable<LlmChatToken> StreamChatAsync(LlmChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter();
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildUri("v1/chat/completions"))
            {
                Content = new StringContent(BuildChatRequestJson(request, stream: true), Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string payload = line["data:".Length..].Trim();
                if (payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                    yield break;

                string text = ReadStreamDelta(payload);
                if (!string.IsNullOrEmpty(text))
                    yield return new LlmChatToken(text);
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private async Task<JsonElement> SendChatRequestAsync(LlmChatRequest request, bool stream, CancellationToken cancellationToken)
    {
        using var operation = _lifetime.Enter();
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = CreateClient();
            using var content = new StringContent(BuildChatRequestJson(request, stream), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(BuildUri("v1/chat/completions"), content, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new LlmRuntimeException("vLLM chat request failed: " + Trim(body, 500), "backend_error", "vllm_chat_failed");

            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private string BuildChatRequestJson(LlmChatRequest request, bool stream)
    {
        var payload = new
        {
            model = _servedModelName,
            messages = request.Messages.Select(message => new
            {
                role = NormalizeRole(message.Role),
                content = message.Content ?? ""
            }).ToArray(),
            stream,
            max_tokens = Math.Clamp(request.MaxTokens, 1, 131072),
            temperature = request.Temperature,
            top_p = request.TopP,
            stop = request.Stop.Count == 0 ? null : request.Stop
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process != null && _process.HasExited)
                throw new LlmRuntimeException(
                    "vLLM exited during startup with code " + _process.ExitCode.ToString(CultureInfo.InvariantCulture) + "." + BuildRecentProcessLogMessage(),
                    "backend_error",
                    "vllm_exited");

            try
            {
                if (await ProbeAsync(cancellationToken).ConfigureAwait(false))
                    return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        throw new LlmRuntimeException(
            "vLLM did not become ready within " + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds." +
            (lastError == null ? "" : " Last error: " + Trim(lastError.Message, 240)) +
            BuildRecentProcessLogMessage(),
            "backend_error",
            "vllm_startup_timeout");
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(5));
        try
        {
            using HttpResponseMessage health = await client.GetAsync(BuildUri("health"), cancellationToken).ConfigureAwait(false);
            if (health.IsSuccessStatusCode)
                return true;
        }
        catch
        {
        }

        try
        {
            using HttpResponseMessage models = await client.GetAsync(BuildUri("v1/models"), cancellationToken).ConfigureAwait(false);
            return models.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private Process StartVllmProcess(string pythonPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = Directory.Exists(_servedModelName) ? _servedModelName : Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in BuildVllmArguments())
            startInfo.ArgumentList.Add(argument);

        string cudaVisibleDevices = BuildCudaVisibleDevices(LoadConfig.TargetDeviceIds);
        if (!string.IsNullOrWhiteSpace(cudaVisibleDevices))
            startInfo.Environment["CUDA_VISIBLE_DEVICES"] = cudaVisibleDevices;
        if (ShouldAllowLongMaxModelLenOverride())
            startInfo.Environment["VLLM_ALLOW_LONG_MAX_MODEL_LEN"] = "1";
        if (ShouldUseLongContextCompatibilityProfile())
        {
            SetEnvironmentDefault(startInfo, "PYTORCH_ALLOC_CONF", "expandable_segments:True");
            SetEnvironmentDefault(startInfo, "NCCL_CUMEM_ENABLE", "0");
            SetEnvironmentDefault(startInfo, "NCCL_NVLS_ENABLE", "0");
            SetEnvironmentDefault(startInfo, "NCCL_IB_DISABLE", "1");
        }

        ClearProcessLog();
        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null for vLLM.");
        _ = Task.Run(() => DrainReaderAsync(process.StandardOutput, "stdout"));
        _ = Task.Run(() => DrainReaderAsync(process.StandardError, "stderr"));
        return process;
    }

    internal bool ShouldAllowLongMaxModelLenOverride()
    {
        uint requestedContextLength = LoadConfig.ContextLength;
        if (requestedContextLength == 0 || !Directory.Exists(_servedModelName))
            return false;

        uint? tokenizerMaxLength = ReadJsonUInt32(Path.Combine(_servedModelName, "tokenizer_config.json"), "model_max_length");
        if (!tokenizerMaxLength.HasValue || tokenizerMaxLength.Value < requestedContextLength)
            return false;

        uint? configMaxLength = ReadLargestJsonUInt32(
            Path.Combine(_servedModelName, "config.json"),
            "max_position_embeddings",
            "seq_length",
            "n_positions",
            "model_max_length");
        return configMaxLength.HasValue && requestedContextLength > configMaxLength.Value;
    }

    internal bool ShouldUseLongContextCompatibilityProfile()
    {
        if (LoadConfig.ContextLength < LongContextCompatibilityThreshold || !ShouldAllowLongMaxModelLenOverride())
            return false;

        string modelIdentity = InstanceId + " " + Path.GetFileName(_servedModelName);
        return modelIdentity.Contains("qwen", StringComparison.OrdinalIgnoreCase) &&
            modelIdentity.Contains("gptq", StringComparison.OrdinalIgnoreCase);
    }

    private static uint? ReadLargestJsonUInt32(string path, params string[] keys)
    {
        var values = new List<uint>();
        foreach (string key in keys)
        {
            uint? value = ReadJsonUInt32(path, key);
            if (value.HasValue && value.Value > 0)
                values.Add(value.Value);
        }

        return values.Count == 0 ? null : values.Max();
    }

    private static uint? ReadJsonUInt32(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
                return null;
            return value.TryGetUInt32(out uint result) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    internal IReadOnlyList<string> BuildVllmArguments()
    {
        string[] extraArguments = SplitCommandLine(_options.VllmExtraArguments).ToArray();
        bool useLongContextProfile = ShouldUseLongContextCompatibilityProfile();
        var arguments = new List<string>
        {
            "-m",
            "vllm.entrypoints.openai.api_server",
            "--model",
            _servedModelName,
            "--port",
            ResolvePort(_baseUri).ToString(CultureInfo.InvariantCulture)
        };

        if (LoadConfig.ContextLength > 0 && !ContainsOption(extraArguments, "--max-model-len"))
        {
            arguments.Add("--max-model-len");
            arguments.Add(LoadConfig.ContextLength.ToString(CultureInfo.InvariantCulture));
        }

        int tensorParallelSize = ResolveTensorParallelSize();
        if (tensorParallelSize > 1 && !ContainsOption(extraArguments, "--tensor-parallel-size"))
        {
            arguments.Add("--tensor-parallel-size");
            arguments.Add(tensorParallelSize.ToString(CultureInfo.InvariantCulture));
        }

        if (LoadConfig.ContextLength > 0 && !ContainsOption(extraArguments, "--max-num-batched-tokens", "--max_num_batched_tokens"))
        {
            arguments.Add("--max-num-batched-tokens");
            uint maxBatchedTokens = useLongContextProfile
                ? Math.Min(LoadConfig.ContextLength, LongContextMaxBatchedTokens)
                : LoadConfig.ContextLength;
            arguments.Add(maxBatchedTokens.ToString(CultureInfo.InvariantCulture));
        }

        if (!ContainsOption(extraArguments, "--gpu-memory-utilization", "--gpu_memory_utilization"))
        {
            arguments.Add("--gpu-memory-utilization");
            double gpuMemoryUtilization = ResolveGpuMemoryUtilization(LoadConfig.MaxVramUsagePercent);
            if (useLongContextProfile)
                gpuMemoryUtilization = Math.Min(gpuMemoryUtilization, LongContextGpuMemoryUtilization);
            arguments.Add(gpuMemoryUtilization.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (useLongContextProfile)
        {
            AddFlagIfMissing(arguments, extraArguments, "--enable-chunked-prefill");
            AddFlagIfMissing(arguments, extraArguments, "--disable-custom-all-reduce");
            AddOptionIfMissing(arguments, extraArguments, "--max-num-seqs", "1", "--max_num_seqs");
            AddOptionIfMissing(arguments, extraArguments, "--cpu-offload-gb", LongContextCpuOffloadGb.ToString("0.###", CultureInfo.InvariantCulture), "--cpu_offload_gb");
            AddOptionIfMissing(arguments, extraArguments, "--kv-cache-dtype", "fp8_e5m2", "--kv_cache_dtype");
        }

        arguments.AddRange(extraArguments);
        return arguments;
    }

    private int ResolveTensorParallelSize()
    {
        if (LoadConfig.TensorParallelSize > 1)
            return LoadConfig.TensorParallelSize;

        int targetCount = LoadConfig.TargetDeviceIds
            .Select(NormalizeCudaDeviceIndex)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (targetCount > 1)
            return targetCount;

        int configured = ReadPositiveIntEnvironment("JACKLLM_VLLM_TENSOR_PARALLEL_SIZE", "LLMRUNTIME_VLLM_TENSOR_PARALLEL_SIZE");
        if (configured > 1)
            return configured;

        long modelBytes = EstimateDirectoryBytes(_servedModelName);
        if (modelBytes < 24L * 1024L * 1024L * 1024L)
            return 1;

        int gpuCount = DetectNvidiaGpuCount();
        return gpuCount > 1 ? gpuCount : 1;
    }

    private static string BuildCudaVisibleDevices(IReadOnlyList<string> deviceIds)
    {
        var devices = deviceIds
            .Select(NormalizeCudaDeviceIndex)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return devices.Length == 0 ? "" : string.Join(",", devices);
    }

    private static string NormalizeCudaDeviceIndex(string value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith("cuda:", StringComparison.OrdinalIgnoreCase))
            text = text["cuda:".Length..].Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 0
            ? index.ToString(CultureInfo.InvariantCulture)
            : "";
    }

    private static int ReadPositiveIntEnvironment(params string[] names)
    {
        foreach (string name in names)
        {
            string value = Environment.GetEnvironmentVariable(name) ?? "";
            if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result > 0)
                return result;
        }

        return 0;
    }

    private static int DetectNvidiaGpuCount()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("JACKLLM_NVIDIA_SMI") ??
                               Environment.GetEnvironmentVariable("NVIDIA_SMI_PATH") ??
                               "nvidia-smi",
                    Arguments = "--query-gpu=index --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
                return 0;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return 0;
            }

            if (process.ExitCode != 0)
                return 0;
            return process.StandardOutput.ReadToEnd()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(line => int.TryParse(line, out _));
        }
        catch
        {
            return 0;
        }
    }

    internal static double ResolveGpuMemoryUtilization(int maxVramUsagePercent)
    {
        if (maxVramUsagePercent <= 0)
            return 0.90d;
        return Math.Clamp(maxVramUsagePercent / 100d, 0.05d, 0.90d);
    }

    private static long EstimateDirectoryBytes(string directory)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", DirectorySizeEnumerationOptions))
            {
                try { total += GetFileLengthFollowingLinks(file); } catch { }
            }
        }
        catch
        {
        }

        return total;
    }

    private static long GetFileLengthFollowingLinks(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
            return 0;

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            try
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is FileInfo targetFile && targetFile.Exists)
                    return targetFile.Length;
            }
            catch
            {
            }
        }

        return info.Length;
    }

    private async Task DrainReaderAsync(StreamReader reader, string source)
    {
        try
        {
            while (!reader.EndOfStream)
                AppendProcessLogLine(source, await reader.ReadLineAsync().ConfigureAwait(false));
        }
        catch
        {
        }
    }

    private void ClearProcessLog()
    {
        lock (_processLogLock)
            _processLogLines.Clear();
    }

    private void AppendProcessLogLine(string source, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_processLogLock)
        {
            _processLogLines.Enqueue(source + ": " + line.Trim());
            while (_processLogLines.Count > ProcessLogLineLimit)
                _processLogLines.Dequeue();
        }
    }

    private string BuildRecentProcessLogMessage()
    {
        string[] lines;
        lock (_processLogLock)
            lines = _processLogLines.ToArray();

        return lines.Length == 0
            ? ""
            : " Recent vLLM log: " + Trim(string.Join(" | ", lines), 1200);
    }

    private HttpClient CreateClient(TimeSpan? timeout = null) => new()
    {
        Timeout = timeout ?? TimeSpan.FromMinutes(10)
    };

    private Uri BuildUri(string relative)
    {
        string normalized = relative.TrimStart('/');
        return new Uri(_baseUri, normalized);
    }

    private void MarkReady(string detail)
    {
        _promptPipelineReady = true;
        _promptPipelineStatus = "ready";
        _promptPipelineDetail = detail;
        _promptPipelineReadyAtUtc = DateTimeOffset.UtcNow;
    }

    private static Uri NormalizeBaseUri(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:8000" : value.Trim();
        if (!text.EndsWith("/", StringComparison.Ordinal))
            text += "/";
        return new Uri(text, UriKind.Absolute);
    }

    private static int ResolvePort(Uri uri) => uri.Port > 0 ? uri.Port : uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    private static bool ContainsOption(IReadOnlyList<string> arguments, params string[] names)
    {
        foreach (string argument in arguments)
        {
            foreach (string name in names)
            {
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddFlagIfMissing(List<string> arguments, IReadOnlyList<string> extraArguments, string option)
    {
        if (!ContainsOption(extraArguments, option))
            arguments.Add(option);
    }

    private static void AddOptionIfMissing(List<string> arguments, IReadOnlyList<string> extraArguments, string option, string value, params string[] aliases)
    {
        var names = new string[aliases.Length + 1];
        names[0] = option;
        Array.Copy(aliases, 0, names, 1, aliases.Length);
        if (ContainsOption(extraArguments, names))
            return;

        arguments.Add(option);
        arguments.Add(value);
    }

    private static void SetEnvironmentDefault(ProcessStartInfo startInfo, string name, string value)
    {
        if (!startInfo.Environment.TryGetValue(name, out string? current) || string.IsNullOrWhiteSpace(current))
            startInfo.Environment[name] = value;
    }

    private static string ResolveModelDirectory(string modelPath)
    {
        string path = Path.GetFullPath(modelPath);
        if (Directory.Exists(path))
            return path;
        if (File.Exists(path))
            return Path.GetDirectoryName(path) ?? path;
        return path;
    }

    private static string ResolvePythonPath(string configured)
    {
        foreach (string candidate in new[]
        {
            configured,
            "/opt/vllm/venv/bin/python3",
            "/opt/vllm/venv/bin/python",
            "python3",
            "python"
        })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
            {
                if (File.Exists(candidate))
                    return candidate;
                continue;
            }

            return candidate;
        }

        return "";
    }

    private static string NormalizeRole(string role)
    {
        if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
            return "system";
        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            return "assistant";
        if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            return "tool";
        return "user";
    }

    private static string ReadStreamDelta(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return "";
            }

            JsonElement first = choices[0];
            if (first.TryGetProperty("delta", out JsonElement delta))
                return ReadString(delta, "content");
            if (first.TryGetProperty("message", out JsonElement message))
                return ReadString(message, "content");
        }
        catch
        {
        }

        return "";
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static IEnumerable<string> SplitCommandLine(string value)
    {
        var builder = new StringBuilder();
        bool inQuotes = false;
        char quote = '\0';
        foreach (char c in value ?? "")
        {
            if ((c == '"' || c == '\'') && (!inQuotes || quote == c))
            {
                inQuotes = !inQuotes;
                quote = inQuotes ? c : '\0';
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }
                continue;
            }

            builder.Append(c);
        }

        if (builder.Length > 0)
            yield return builder.ToString();
    }

    private static string Trim(string text, int maxLength)
    {
        text = (text ?? "").Trim();
        return text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
    }

    public void Dispose()
    {
        if (!_lifetime.BeginDisposeAndWait())
            return;

        try
        {
            if (_process != null && !_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            _process?.Dispose();
            _process = null;
            _inferenceLock.Dispose();
        }
        finally
        {
            _lifetime.CompleteDispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
