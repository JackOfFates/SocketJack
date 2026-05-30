using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace LlmRuntime;

public sealed class LlmToolInvocationRequest
{
    public string ToolId { get; set; } = "";

    public string ToolCallId { get; set; } = "";

    public JsonElement Input { get; set; } = JsonDocument.Parse("{}").RootElement.Clone();

    public bool Approved { get; set; }

    public string ProjectPath { get; set; } = "";
}

public sealed class LlmToolInvocationResult
{
    public bool Success { get; set; }

    public string ToolId { get; set; } = "";

    public string OutputText { get; set; } = "";

    public JsonElement? OutputJson { get; set; }

    public string Error { get; set; } = "";

    public TimeSpan Elapsed { get; set; }

    public LlmToolPermissions RequiredPermissions { get; set; }

    public bool ApprovalRequired { get; set; }
}

public interface ILlmToolInvoker
{
    Task<LlmToolInvocationResult> InvokeAsync(LlmToolInvocationRequest request, CancellationToken cancellationToken = default);
}

public sealed class LlmToolInvoker : ILlmToolInvoker
{
    private readonly LlmToolRegistry _registry;
    private readonly HttpClient _httpClient;
    private readonly LlmToolSafetyPolicy _safetyPolicy;
    private readonly Dictionary<string, ILlmTool> _builtInTools;

    public LlmToolInvoker(LlmToolRegistry registry, HttpClient? httpClient = null, LlmToolSafetyPolicy? safetyPolicy = null, IEnumerable<ILlmTool>? builtInTools = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _httpClient = httpClient ?? new HttpClient();
        _safetyPolicy = safetyPolicy ?? new LlmToolSafetyPolicy();
        _builtInTools = (builtInTools ?? [new WindowsDesktopAutomationTool()])
            .Where(tool => tool != null)
            .ToDictionary(tool => LlmToolRegistry.NormalizeId(tool.Id), tool => tool, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<LlmToolInvocationResult> InvokeAsync(LlmToolInvocationRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var stopwatch = Stopwatch.StartNew();
        var definition = _registry.GetDefinition(request.ToolId);
        if (definition == null)
            return Failed(request.ToolId, "Tool definition was not found.", stopwatch.Elapsed);

        var secrets = ResolveSecrets(definition);
        var redactor = new LlmToolRedactor(secrets.Values);
        var safety = _safetyPolicy.Validate(definition, request, secrets);
        if (!safety.Allowed)
        {
            var denied = Failed(definition.Id, safety.DenialReason, stopwatch.Elapsed);
            denied.RequiredPermissions = LlmToolSafetyPolicy.RequiredPermissions(definition);
            denied.ApprovalRequired = IsApprovalRequired(definition);
            Audit(definition, "invoke", "denied", denied.Error, redactor);
            return denied;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(definition.TimeoutSeconds, 1, 3600)));

        try
        {
            LlmToolInvocationResult result = definition.SourceType switch
            {
                LlmToolSourceType.Http => await InvokeHttpAsync(definition, request, secrets, redactor, timeoutCts.Token).ConfigureAwait(false),
                LlmToolSourceType.Executable => await InvokeProcessAsync(definition, request, secrets, redactor, usePowerShell: false, timeoutCts.Token).ConfigureAwait(false),
                LlmToolSourceType.PowerShell => await InvokeProcessAsync(definition, request, secrets, redactor, usePowerShell: true, timeoutCts.Token).ConfigureAwait(false),
                LlmToolSourceType.NamedPipe => await InvokeNamedPipeAsync(definition, request, secrets, redactor, timeoutCts.Token).ConfigureAwait(false),
                LlmToolSourceType.DotNetAssembly => await InvokeDotNetAssemblyAsync(definition, request, secrets, redactor, timeoutCts.Token).ConfigureAwait(false),
                LlmToolSourceType.McpServer => await InvokeMcpServerAsync(definition, request, secrets, redactor, timeoutCts.Token).ConfigureAwait(false),
                LlmToolSourceType.BuiltInSocketJack => await InvokeBuiltInAsync(definition, request, timeoutCts.Token).ConfigureAwait(false),
                _ => Failed(definition.Id, definition.SourceType + " invocation is not implemented yet.", stopwatch.Elapsed)
            };

            result.Elapsed = stopwatch.Elapsed;
            result.RequiredPermissions = safety.RequiredPermissions;
            result.ApprovalRequired = IsApprovalRequired(definition);
            Audit(definition, "invoke", result.Success ? "success" : "failed", result.Success ? "Tool invocation completed." : result.Error, redactor);
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = Failed(definition.Id, "Tool invocation timed out or was canceled.", stopwatch.Elapsed);
            result.RequiredPermissions = safety.RequiredPermissions;
            result.ApprovalRequired = IsApprovalRequired(definition);
            Audit(definition, "invoke", "canceled", result.Error, redactor);
            return result;
        }
        catch (Exception ex)
        {
            var result = Failed(definition.Id, redactor.Redact(ex.Message), stopwatch.Elapsed);
            result.RequiredPermissions = safety.RequiredPermissions;
            result.ApprovalRequired = IsApprovalRequired(definition);
            Audit(definition, "invoke", "failed", result.Error, redactor);
            return result;
        }
    }

    private async Task<LlmToolInvocationResult> InvokeBuiltInAsync(LlmToolDefinition definition, LlmToolInvocationRequest request, CancellationToken cancellationToken)
    {
        string source = LlmToolRegistry.NormalizeId(string.IsNullOrWhiteSpace(definition.Source) ? definition.Id : definition.Source);
        if (!_builtInTools.TryGetValue(source, out var tool) && !_builtInTools.TryGetValue(LlmToolRegistry.NormalizeId(definition.Id), out tool))
            return Failed(definition.Id, "Built-in SocketJack tool is not registered: " + definition.Source, TimeSpan.Zero);

        var result = await tool.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        result.ToolId = definition.Id;
        return result;
    }

    private async Task<LlmToolInvocationResult> InvokeHttpAsync(LlmToolDefinition definition, LlmToolInvocationRequest request, IReadOnlyDictionary<string, string> secrets, LlmToolRedactor redactor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Source))
            return Failed(definition.Id, "HTTP tool source URL is required.", TimeSpan.Zero);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ExpandSecrets(definition.Source, definition, secrets));
        foreach (var header in definition.HttpHeaders)
        {
            string value = ExpandSecrets(header.Value, definition, secrets);
            if (!httpRequest.Headers.TryAddWithoutValidation(header.Key, value))
                httpRequest.Content?.Headers.TryAddWithoutValidation(header.Key, value);
        }

        using var content = new StringContent(request.Input.GetRawText(), Encoding.UTF8, "application/json");
        httpRequest.Content = content;
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return BuildResult(definition.Id, response.IsSuccessStatusCode, redactor.Redact(body), response.IsSuccessStatusCode ? "" : "HTTP " + (int)response.StatusCode);
    }

    private static async Task<LlmToolInvocationResult> InvokeProcessAsync(LlmToolDefinition definition, LlmToolInvocationRequest request, IReadOnlyDictionary<string, string> secrets, LlmToolRedactor redactor, bool usePowerShell, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Source))
            return Failed(definition.Id, "Executable or PowerShell source is required.", TimeSpan.Zero);

        string source = ExpandSecrets(definition.Source, definition, secrets);
        var startInfo = usePowerShell
            ? CreatePowerShellStartInfo(source)
            : new ProcessStartInfo(source);

        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        foreach (var pair in definition.EnvironmentVariables)
            startInfo.Environment[pair.Key] = ExpandSecrets(pair.Value, definition, secrets);

        using var process = Process.Start(startInfo);
        if (process == null)
            return Failed(definition.Id, "Process could not be started.", TimeSpan.Zero);

        await process.StandardInput.WriteAsync(request.Input.GetRawText()).ConfigureAwait(false);
        process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return BuildResult(definition.Id, process.ExitCode == 0, redactor.Redact(output), redactor.Redact(error));
    }

    private static async Task<LlmToolInvocationResult> InvokeNamedPipeAsync(LlmToolDefinition definition, LlmToolInvocationRequest request, IReadOnlyDictionary<string, string> secrets, LlmToolRedactor redactor, CancellationToken cancellationToken)
    {
        string source = ExpandSecrets(definition.Source, definition, secrets);
        if (string.IsNullOrWhiteSpace(source))
            return Failed(definition.Id, "Named pipe source is required.", TimeSpan.Zero);

        string pipeName = NormalizePipeName(source);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

        byte[] requestBytes = Encoding.UTF8.GetBytes(request.Input.GetRawText() + Environment.NewLine);
        await pipe.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var outputBuffer = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = await pipe.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (outputBuffer.Length > 0)
            {
                break;
            }

            if (read == 0)
                break;

            outputBuffer.Write(buffer, 0, read);
        }

        string output = Encoding.UTF8.GetString(outputBuffer.ToArray());
        return BuildResult(definition.Id, true, redactor.Redact(output), "");
    }

    private static async Task<LlmToolInvocationResult> InvokeDotNetAssemblyAsync(LlmToolDefinition definition, LlmToolInvocationRequest request, IReadOnlyDictionary<string, string> secrets, LlmToolRedactor redactor, CancellationToken cancellationToken)
    {
        string source = ExpandSecrets(definition.Source, definition, secrets);
        if (!TryParseAssemblySource(source, out string assemblyPath, out string typeName, out string methodName))
        {
            return Failed(
                definition.Id,
                "DotNetAssembly source must use: C:\\path\\Tool.dll::Namespace.Type.Method",
                TimeSpan.Zero);
        }

        if (!File.Exists(assemblyPath))
            return Failed(definition.Id, "DotNetAssembly file was not found: " + assemblyPath, TimeSpan.Zero);

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (type == null)
            return Failed(definition.Id, "DotNetAssembly type was not found: " + typeName, TimeSpan.Zero);

        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        if (method == null)
            return Failed(definition.Id, "DotNetAssembly method was not found: " + typeName + "." + methodName, TimeSpan.Zero);

        object? value = method.Invoke(null, BuildAssemblyArguments(method, request, cancellationToken));
        if (value is Task task)
        {
            await task.ConfigureAwait(false);
            value = task.GetType().IsGenericType ? task.GetType().GetProperty("Result")?.GetValue(task) : null;
        }

        string output = SerializeToolReturnValue(value);
        return BuildResult(definition.Id, true, redactor.Redact(output), "");
    }

    private async Task<LlmToolInvocationResult> InvokeMcpServerAsync(LlmToolDefinition definition, LlmToolInvocationRequest request, IReadOnlyDictionary<string, string> secrets, LlmToolRedactor redactor, CancellationToken cancellationToken)
    {
        string source = ExpandSecrets(definition.Source, definition, secrets);
        if (string.IsNullOrWhiteSpace(source))
            return Failed(definition.Id, "MCP server source URL is required.", TimeSpan.Zero);

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Failed(definition.Id, "MCP server invocation currently supports HTTP/HTTPS JSON-RPC endpoints.", TimeSpan.Zero);

        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "tool_" + Guid.NewGuid().ToString("N"),
            method = "tools/call",
            @params = new
            {
                name = definition.Id,
                arguments = request.Input
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return BuildResult(definition.Id, response.IsSuccessStatusCode, redactor.Redact(body), response.IsSuccessStatusCode ? "" : "HTTP " + (int)response.StatusCode);
    }

    private static LlmToolInvocationResult BuildResult(string toolId, bool success, string output, string error)
    {
        JsonElement? outputJson = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(output))
                outputJson = JsonDocument.Parse(output).RootElement.Clone();
        }
        catch
        {
        }

        return new LlmToolInvocationResult
        {
            Success = success,
            ToolId = toolId,
            OutputText = output ?? "",
            OutputJson = outputJson,
            Error = error ?? ""
        };
    }

    private static LlmToolInvocationResult Failed(string toolId, string error, TimeSpan elapsed) =>
        new()
        {
            Success = false,
            ToolId = toolId,
            Error = error,
            Elapsed = elapsed
        };

    private void Audit(LlmToolDefinition definition, string action, string outcome, string message, LlmToolRedactor redactor)
    {
        _registry.AddAuditEntry(new LlmToolAuditEntry
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Action = action,
            Outcome = outcome,
            Message = redactor.Redact(message)
        });
    }

    private Dictionary<string, string> ResolveSecrets(LlmToolDefinition definition)
    {
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in definition.RequiredSecrets)
        {
            string? value = _registry.GetSecret(secret.SecretId);
            if (value == null)
                continue;

            secrets[secret.SecretId] = value;
            secrets[secret.Name] = value;
        }
        return secrets;
    }

    private static bool IsApprovalRequired(LlmToolDefinition definition) =>
        definition.ApprovalMode == LlmToolApprovalMode.AskEveryTime
        || definition.ApprovalMode == LlmToolApprovalMode.AskOnDestructiveOperations;

    private static string ExpandSecrets(string value, LlmToolDefinition definition, IReadOnlyDictionary<string, string> secrets)
    {
        if (string.IsNullOrEmpty(value) || definition.RequiredSecrets.Count == 0)
            return value ?? "";

        string expanded = value;
        foreach (var secret in definition.RequiredSecrets)
        {
            if (!secrets.TryGetValue(secret.SecretId, out string? secretValue)
                && !secrets.TryGetValue(secret.Name, out secretValue))
            {
                continue;
            }

            expanded = expanded.Replace("{{secret:" + secret.Name + "}}", secretValue, StringComparison.OrdinalIgnoreCase)
                .Replace("{{secret:" + secret.SecretId + "}}", secretValue, StringComparison.OrdinalIgnoreCase)
                .Replace("${secret:" + secret.Name + "}", secretValue, StringComparison.OrdinalIgnoreCase)
                .Replace("${secret:" + secret.SecretId + "}", secretValue, StringComparison.OrdinalIgnoreCase);
        }
        return expanded;
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static ProcessStartInfo CreatePowerShellStartInfo(string command)
    {
        if (OperatingSystem.IsWindows())
            return new ProcessStartInfo("powershell", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(command));

        return new ProcessStartInfo("pwsh", "-NoProfile -Command " + QuoteArgument(command));
    }

    private static string NormalizePipeName(string source)
    {
        const string prefix = @"\\.\pipe\";
        return source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? source[prefix.Length..] : source;
    }

    private static bool TryParseAssemblySource(string source, out string assemblyPath, out string typeName, out string methodName)
    {
        assemblyPath = "";
        typeName = "";
        methodName = "";
        if (string.IsNullOrWhiteSpace(source))
            return false;

        int delimiter = source.IndexOf("::", StringComparison.Ordinal);
        if (delimiter <= 0 || delimiter >= source.Length - 2)
            return false;

        assemblyPath = source[..delimiter].Trim().Trim('"');
        string member = source[(delimiter + 2)..].Trim();
        int methodDelimiter = member.LastIndexOf('.');
        if (methodDelimiter <= 0 || methodDelimiter >= member.Length - 1)
            return false;

        typeName = member[..methodDelimiter];
        methodName = member[(methodDelimiter + 1)..];
        return true;
    }

    private static object?[] BuildAssemblyArguments(MethodInfo method, LlmToolInvocationRequest request, CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (type == typeof(JsonElement))
                values[i] = request.Input;
            else if (type == typeof(string))
                values[i] = request.Input.GetRawText();
            else if (type == typeof(LlmToolInvocationRequest))
                values[i] = request;
            else if (type == typeof(CancellationToken))
                values[i] = cancellationToken;
            else
                values[i] = request.Input.Deserialize(type, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        return values;
    }

    private static string SerializeToolReturnValue(object? value)
    {
        if (value == null)
            return "{}";
        if (value is string text)
            return text;
        if (value is JsonElement json)
            return json.GetRawText();
        return JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
