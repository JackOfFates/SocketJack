using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Com
{
    public static class SocketJackComFeatures
    {
        public const string Chat = "chat";
        public const string Vision = "vision";
        public const string ImageAnalysis = "image-analysis";
        public const string VideoAnalysis = "video-analysis";
        public const string AudioAnalysis = "audio-analysis";
        public const string ImageGeneration = "image-generation";
        public const string AudioGeneration = "audio-generation";
        public const string VideoGeneration = "video-generation";
        public const string Tools = "tools";
    }

    public sealed class SocketJackComRequirements
    {
        public bool RequireChat { get; set; } = true;
        public bool RequireVision { get; set; }
        public bool RequireImageAnalysis { get; set; }
        public bool RequireVideoAnalysis { get; set; }
        public bool RequireAudioAnalysis { get; set; }
        public bool RequireImageGeneration { get; set; }
        public bool RequireAudioGeneration { get; set; }
        public bool RequireVideoGeneration { get; set; }
        public List<string> RequiredTools { get; set; } = new List<string>();

        public static SocketJackComRequirements Chat()
        {
            return new SocketJackComRequirements { RequireChat = true };
        }

        public static SocketJackComRequirements Vision()
        {
            return new SocketJackComRequirements
            {
                RequireChat = true,
                RequireVision = true,
                RequireImageAnalysis = true
            };
        }

        public static SocketJackComRequirements VideoAnalysis()
        {
            return new SocketJackComRequirements
            {
                RequireChat = true,
                RequireVision = true,
                RequireVideoAnalysis = true
            };
        }

        public static SocketJackComRequirements MediaGeneration(string mediaKind)
        {
            mediaKind = (mediaKind ?? "").Trim().ToLowerInvariant();
            return new SocketJackComRequirements
            {
                RequireChat = false,
                RequireImageGeneration = mediaKind == "image",
                RequireAudioGeneration = mediaKind == "audio",
                RequireVideoGeneration = mediaKind == "video"
            };
        }

        public IEnumerable<string> RequiredFeatures()
        {
            if (RequireChat)
                yield return SocketJackComFeatures.Chat;
            if (RequireVision)
                yield return SocketJackComFeatures.Vision;
            if (RequireImageAnalysis)
                yield return SocketJackComFeatures.ImageAnalysis;
            if (RequireVideoAnalysis)
                yield return SocketJackComFeatures.VideoAnalysis;
            if (RequireAudioAnalysis)
                yield return SocketJackComFeatures.AudioAnalysis;
            if (RequireImageGeneration)
                yield return SocketJackComFeatures.ImageGeneration;
            if (RequireAudioGeneration)
                yield return SocketJackComFeatures.AudioGeneration;
            if (RequireVideoGeneration)
                yield return SocketJackComFeatures.VideoGeneration;
        }
    }

    public sealed class SocketJackComServer
    {
        public string ServerId { get; set; } = "";
        public string Name { get; set; } = "";
        public string OwnerUserName { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string OpenAiBaseUrl { get; set; } = "";
        public string SelectedModel { get; set; } = "";
        public string AvailableModels { get; set; } = "";
        public string ToolsAllowed { get; set; } = "";
        public string HardwareSummary { get; set; } = "";
        public string AvailableRam { get; set; } = "";
        public string AvailableVram { get; set; } = "";
        public string GpuModel { get; set; } = "";
        public string CpuModel { get; set; } = "";
        public long MaxTokens { get; set; }
        public bool Online { get; set; }
        public int HealthScore { get; set; }
        public HashSet<string> Features { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Tools { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ModeModels { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string SourceJson { get; set; } = "";

        public string PublicServerId
        {
            get { return MakePublicServerId(FirstNonEmpty(ServerId, Endpoint, OpenAiBaseUrl, Name)); }
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                    return Name;
                if (!string.IsNullOrWhiteSpace(Endpoint))
                    return Endpoint;
                if (!string.IsNullOrWhiteSpace(OpenAiBaseUrl))
                    return OpenAiBaseUrl;
                return "SocketJack.Com AI server";
            }
        }

        private static string MakePublicServerId(string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            if (value.Length == 0)
                value = "socketjack-com-ai-server";
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return "sjai-" + BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
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
    }

    public sealed class SocketJackComRequest
    {
        public string Prompt { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        public string ImageDataUrl { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string MediaKind { get; set; } = "";
        public string PreferredServerId { get; set; } = "";
        public string Context { get; set; } = "";
        public int MaxTokens { get; set; } = 700;
    }

    public sealed class SocketJackComResult
    {
        public bool Success { get; set; }
        public string Text { get; set; } = "";
        public string Error { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string Model { get; set; } = "";
        public string RawJson { get; set; } = "";
        public string ArtifactUrl { get; set; } = "";
        public string ArtifactId { get; set; } = "";
        public string RenderBackend { get; set; } = "";
        public string BackendLogLine { get; set; } = "";
        public bool UsesGraphicsBackend { get; set; }
    }

    public sealed class SocketJackComClientOptions
    {
        public List<string> MasterListUrls { get; set; } = new List<string>();
        public List<string> DirectEndpoints { get; set; } = new List<string>();
        public string DefaultModel { get; set; } = "";
        public string AuthorizationBearerToken { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 60;
    }

    public sealed class SocketJackComClient
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly SocketJackComClientOptions _options;

        public SocketJackComClient()
            : this(new SocketJackComClientOptions())
        {
        }

        public SocketJackComClient(SocketJackComClientOptions options)
        {
            _options = options ?? new SocketJackComClientOptions();
            AddConfiguredDefaults();
        }

        public async Task<IReadOnlyList<SocketJackComServer>> DiscoverServersAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var servers = new List<SocketJackComServer>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string masterUrl in _options.MasterListUrls)
            {
                string url = NormalizeUrl(masterUrl);
                if (url.Length == 0)
                    continue;

                try
                {
                    using (var request = BuildHttpRequest(HttpMethod.Get, url))
                    using (HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            continue;

                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        foreach (SocketJackComServer server in ParseServerList(json))
                        {
                            if (!TryReserveServerIdentity(seen, server))
                                continue;

                            servers.Add(server);
                        }
                    }
                }
                catch
                {
                }
            }

            foreach (string directEndpoint in _options.DirectEndpoints)
            {
                string endpoint = NormalizeBaseUrl(directEndpoint);
                if (endpoint.Length == 0)
                    continue;

                var direct = new SocketJackComServer
                {
                    ServerId = endpoint,
                    Name = endpoint.Contains("localhost") || endpoint.Contains("127.0.0.1") ? "Local SocketJack.Com server" : "Direct SocketJack.Com server",
                    Endpoint = endpoint,
                    OpenAiBaseUrl = endpoint,
                    Online = true,
                    HealthScore = 70
                };
                direct.Features.Add(SocketJackComFeatures.Chat);
                direct.Features.Add(SocketJackComFeatures.Vision);
                direct.Features.Add(SocketJackComFeatures.ImageAnalysis);
                direct.Features.Add(SocketJackComFeatures.VideoAnalysis);
                direct.Features.Add(SocketJackComFeatures.ImageGeneration);
                direct.Features.Add(SocketJackComFeatures.AudioGeneration);
                direct.Features.Add(SocketJackComFeatures.VideoGeneration);
                if (TryReserveServerIdentity(seen, direct))
                    servers.Add(direct);
            }

            return servers
                .OrderByDescending(server => server.Online)
                .ThenByDescending(server => server.HealthScore)
                .ThenBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<SocketJackComServer> SelectFirstAvailableServerAsync(SocketJackComRequirements requirements, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await SelectAvailableServerAsync(requirements, "", cancellationToken).ConfigureAwait(false);
        }

        public async Task<SocketJackComServer> SelectAvailableServerAsync(SocketJackComRequirements requirements, string preferredServerId, CancellationToken cancellationToken = default(CancellationToken))
        {
            requirements = requirements ?? SocketJackComRequirements.Chat();
            IReadOnlyList<SocketJackComServer> servers = await DiscoverServersAsync(cancellationToken).ConfigureAwait(false);
            preferredServerId = (preferredServerId ?? "").Trim();
            if (preferredServerId.Length > 0)
            {
                SocketJackComServer preferred = servers.FirstOrDefault(server => ServerIdMatches(server, preferredServerId) && Matches(server, requirements));
                if (preferred != null)
                    return preferred;
            }
            return servers.FirstOrDefault(server => Matches(server, requirements));
        }

        public async Task<SocketJackComResult> ChatAsync(SocketJackComRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            request = request ?? new SocketJackComRequest();
            SocketJackComRequirements requirements = string.IsNullOrWhiteSpace(request.ImageDataUrl) && string.IsNullOrWhiteSpace(request.ImageUrl)
                ? SocketJackComRequirements.Chat()
                : SocketJackComRequirements.Vision();

            SocketJackComServer server = await SelectAvailableServerAsync(requirements, request.PreferredServerId, cancellationToken).ConfigureAwait(false);
            if (server == null)
                return Failure(requirements.RequireVision
                    ? "No SocketJack.Com server is available with the required chat/vision features."
                    : "No SocketJack.Com server is available with chat.");

            return await ChatWithServerAsync(server, request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<SocketJackComResult> AnalyzeImageAsync(SocketJackComRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            request = request ?? new SocketJackComRequest();
            if (string.IsNullOrWhiteSpace(request.Prompt))
                request.Prompt = "Describe this live video frame clearly. Mention visible action, scene changes, text, people, objects, and anything the streamer might want called out.";

            SocketJackComServer server = await SelectAvailableServerAsync(SocketJackComRequirements.Vision(), request.PreferredServerId, cancellationToken).ConfigureAwait(false);
            if (server == null)
                return Failure("No SocketJack.Com server is available with vision/image analysis.");

            return await ChatWithServerAsync(server, request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<SocketJackComResult> GenerateAsync(SocketJackComRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            request = request ?? new SocketJackComRequest();
            string mediaKind = NormalizeMediaKind(request.MediaKind);
            if (mediaKind.Length == 0)
                return Failure("MediaKind must be image, audio, or video.");

            SocketJackComServer server = await SelectAvailableServerAsync(SocketJackComRequirements.MediaGeneration(mediaKind), request.PreferredServerId, cancellationToken).ConfigureAwait(false);
            if (server == null)
                return Failure("No SocketJack.Com server is available with " + mediaKind + " generation.");

            SocketJackComResult generated = await TryJackOnnxGenerationAsync(server, request, mediaKind, cancellationToken).ConfigureAwait(false);
            if (generated.Success || !string.IsNullOrWhiteSpace(generated.RawJson))
                return generated;

            var explainRequest = new SocketJackComRequest
            {
                Prompt = "The user asked to generate " + mediaKind + ": " + request.Prompt + Environment.NewLine +
                         "If native generation tools are not available, respond with the exact SocketJack.Com/JackONNX tool or model needed and a concise fallback prompt.",
                SystemPrompt = "You are JackCast AI. Be direct and practical.",
                Model = request.Model,
                MaxTokens = request.MaxTokens
            };
            SocketJackComResult fallback = await ChatWithServerAsync(server, explainRequest, cancellationToken).ConfigureAwait(false);
            fallback.Error = generated.Error;
            return fallback;
        }

        private async Task<SocketJackComResult> ChatWithServerAsync(SocketJackComServer server, SocketJackComRequest request, CancellationToken cancellationToken)
        {
            string jackLlmEndpoint = NormalizeBaseUrl(server.Endpoint);
            string loadedModel = jackLlmEndpoint.Length > 0
                ? await ResolveFirstLoadedModeModelAsync(jackLlmEndpoint, SocketJackComFeatures.Chat, cancellationToken).ConfigureAwait(false)
                : "";
            string model = FirstNonEmpty(loadedModel, request.Model, FirstModeModel(server, SocketJackComFeatures.Chat), server.SelectedModel, _options.DefaultModel, FirstModel(server.AvailableModels), "local-model");
            string endpoint = NormalizeBaseUrl(server.OpenAiBaseUrl);
            if (endpoint.Length > 0)
            {
                SocketJackComResult openAiResult = await TryOpenAiChatAsync(server, endpoint, model, request, cancellationToken).ConfigureAwait(false);
                if (openAiResult.Success || !string.IsNullOrWhiteSpace(openAiResult.RawJson))
                    return openAiResult;
            }

            endpoint = NormalizeBaseUrl(server.Endpoint);
            if (endpoint.Length == 0)
                return Failure("Selected SocketJack.Com server has no endpoint.");

            return await TrySocketJackChatUiAsync(server, endpoint, model, request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<SocketJackComResult> TryOpenAiChatAsync(SocketJackComServer server, string baseUrl, string model, SocketJackComRequest request, CancellationToken cancellationToken)
        {
            try
            {
                object content = BuildOpenAiUserContent(request);
                var body = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = FirstNonEmpty(request.SystemPrompt, "You are JackCast AI.") },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = content }
                    },
                    ["max_tokens"] = request.MaxTokens <= 0 ? 700 : request.MaxTokens
                };

                string url = baseUrl.TrimEnd('/') + "/v1/chat/completions";
                string raw = await PostJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
                string text = ExtractOpenAiText(raw);
                return new SocketJackComResult
                {
                    Success = text.Length > 0,
                    Text = text,
                    Error = text.Length > 0 ? "" : "The OpenAI-compatible server returned no assistant text.",
                    ServerName = server.DisplayName,
                    Endpoint = baseUrl,
                    Model = model,
                    RawJson = raw
                };
            }
            catch (Exception ex)
            {
                return new SocketJackComResult
                {
                    Success = false,
                    Error = ex.Message,
                    ServerName = server.DisplayName,
                    Endpoint = baseUrl,
                    Model = model
                };
            }
        }

        private async Task<SocketJackComResult> TrySocketJackChatUiAsync(SocketJackComServer server, string endpoint, string model, SocketJackComRequest request, CancellationToken cancellationToken)
        {
            SocketJackComResult streamResult = await TrySocketJackChatUiStreamAsync(server, endpoint, model, request, cancellationToken).ConfigureAwait(false);
            if (streamResult.Success || !string.IsNullOrWhiteSpace(streamResult.RawJson))
                return streamResult;

            try
            {
                var body = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = FirstNonEmpty(request.SystemPrompt, "You are JackCast AI.") },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = BuildSocketJackChatUiUserContent(request) }
                    }
                };

                string raw = await PostJsonAsync(endpoint.TrimEnd('/') + "/api/chat", body, cancellationToken).ConfigureAwait(false);
                string text = ExtractSocketJackChatUiText(raw);
                return new SocketJackComResult
                {
                    Success = text.Length > 0,
                    Text = text,
                    Error = text.Length > 0 ? "" : "The SocketJack chat server returned no assistant text.",
                    ServerName = server.DisplayName,
                    Endpoint = endpoint,
                    Model = model,
                    RawJson = raw
                };
            }
            catch (Exception ex)
            {
                return new SocketJackComResult
                {
                    Success = false,
                    Error = ex.Message,
                    ServerName = server.DisplayName,
                    Endpoint = endpoint,
                    Model = model
                };
            }
        }

        private async Task<SocketJackComResult> TrySocketJackChatUiStreamAsync(SocketJackComServer server, string endpoint, string model, SocketJackComRequest request, CancellationToken cancellationToken)
        {
            string url = endpoint.TrimEnd('/') + "/api/chat-stream";
            string streamId = "stream_" + Guid.NewGuid().ToString("N");
            string sessionId = "jackcast-" + Guid.NewGuid().ToString("N");
            var rawEvents = new StringBuilder();
            var content = new StringBuilder();
            var reasoning = new StringBuilder();

            try
            {
                var body = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = FirstNonEmpty(request.SystemPrompt, "You are JackCast AI. Answer directly and briefly.") },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = BuildSocketJackChatUiUserContent(request) }
                    },
                    ["max_tokens"] = request.MaxTokens <= 0 ? 700 : request.MaxTokens,
                    ["temperature"] = 0.2,
                    ["sessionId"] = sessionId,
                    ["streamId"] = streamId,
                    ["shareKey"] = "",
                    ["files"] = new object[0]
                };

                string json = JsonSerializer.Serialize(body, JsonOptions);
                using (var httpRequest = BuildHttpRequest(HttpMethod.Post, url))
                {
                    httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            bool canFallback = response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                                               response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
                            return new SocketJackComResult
                            {
                                Success = false,
                                Error = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + ": " + errorBody,
                                ServerName = server.DisplayName,
                                Endpoint = endpoint,
                                Model = model,
                                RawJson = canFallback ? "" : errorBody
                            };
                        }

                        using (Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                        {
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line == null)
                                    break;
                                if (string.IsNullOrWhiteSpace(line))
                                    continue;

                                string eventJson = NormalizeStreamEventLine(line);
                                if (eventJson.Length == 0)
                                    continue;

                                if (rawEvents.Length < 16000)
                                    rawEvents.AppendLine(eventJson);

                                using (JsonDocument document = JsonDocument.Parse(eventJson))
                                {
                                    JsonElement root = document.RootElement;
                                    string type = ReadString(root, "type");
                                    if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string error = FirstNonEmpty(ReadString(root, "content"), ReadString(root, "message"), ReadString(root, "error"), ReadString(root, "body"), "SocketJack chat stream failed.");
                                        return new SocketJackComResult
                                        {
                                            Success = false,
                                            Error = error,
                                            ServerName = server.DisplayName,
                                            Endpoint = endpoint,
                                            Model = model,
                                            RawJson = rawEvents.ToString()
                                        };
                                    }
                                    if (type.Equals("done", StringComparison.OrdinalIgnoreCase))
                                        break;

                                    content.Append(ExtractChatUiStreamContent(root));
                                    reasoning.Append(ExtractChatUiStreamReasoning(root));
                                }
                            }
                        }
                    }
                }

                string answer = content.ToString().Trim();
                string fallbackReasoning = reasoning.ToString().Trim();
                string text = FirstNonEmpty(answer, fallbackReasoning);
                return new SocketJackComResult
                {
                    Success = text.Length > 0,
                    Text = text,
                    Error = text.Length > 0 ? "" : "The SocketJack chat stream completed without assistant text.",
                    ServerName = server.DisplayName,
                    Endpoint = endpoint,
                    Model = model,
                    RawJson = rawEvents.ToString()
                };
            }
            catch (Exception ex)
            {
                return new SocketJackComResult
                {
                    Success = false,
                    Error = ex.Message,
                    ServerName = server.DisplayName,
                    Endpoint = endpoint,
                    Model = model,
                    RawJson = rawEvents.ToString()
                };
            }
        }

        private async Task<SocketJackComResult> TryJackOnnxGenerationAsync(SocketJackComServer server, SocketJackComRequest request, string mediaKind, CancellationToken cancellationToken)
        {
            string endpoint = NormalizeBaseUrl(server.Endpoint);
            if (endpoint.Length == 0)
                return Failure("Selected SocketJack.Com server has no JackONNX endpoint.");

            string loadedModel = await ResolveFirstLoadedModeModelAsync(endpoint, mediaKind, cancellationToken).ConfigureAwait(false);
            string model = FirstNonEmpty(loadedModel, request.Model, FirstModeModel(server, mediaKind));

            string path = mediaKind == "image" ? "/api/jackonnx/image/generate" :
                           mediaKind == "audio" ? "/api/jackonnx/audio/generate" :
                           "/api/jackonnx/video/generate";

            string detectedProvider = await DetectJackonnxProviderAsync(endpoint, cancellationToken).ConfigureAwait(false);
            string resolvedProvider = NormalizeProvider(detectedProvider);
            string backendSummary = FormatBackendSummary(resolvedProvider);
            string backendWarning = BackendWarning(resolvedProvider);
            bool usesGraphicsBackend = UsesGpuBackend(resolvedProvider);
            string backendCommandLine = FormatBackendProbeCommandLine(endpoint);
            string backendLogLine = string.Join(Environment.NewLine, new[] { backendCommandLine, backendSummary, backendWarning }.Where(line => !string.IsNullOrWhiteSpace(line)));

            try
            {
                var body = mediaKind == "audio"
                    ? new Dictionary<string, object>
                    {
                        ["text"] = request.Prompt,
                        ["modelId"] = model
                    }
                    : new Dictionary<string, object>
                    {
                        ["prompt"] = request.Prompt,
                        ["modelId"] = model
                    };
                string raw = await PostJsonAsync(endpoint.TrimEnd('/') + path, body, cancellationToken).ConfigureAwait(false);
                string responseProvider = ExtractExecutionProvider(raw);
                resolvedProvider = FirstNonEmpty(responseProvider, detectedProvider);
                backendSummary = FormatBackendSummary(resolvedProvider);
                backendWarning = BackendWarning(resolvedProvider);
                usesGraphicsBackend = UsesGpuBackend(resolvedProvider);
                backendLogLine = string.Join(Environment.NewLine, new[] { backendCommandLine, backendSummary, backendWarning }.Where(line => !string.IsNullOrWhiteSpace(line)));

                string artifactId = ExtractPreferredGenerationArtifactId(raw, mediaKind);
                string generationMessage = ExtractGenerationMessage(raw);
                return new SocketJackComResult
                {
                    Success = IsGenerationSuccess(raw),
                    Text = AppendBackendLogLines(generationMessage, backendSummary, backendWarning),
                    Error = IsGenerationSuccess(raw) ? "" : AppendBackendLogLines(FirstNonEmpty(generationMessage, "Generation failed."), backendSummary, backendWarning),
                    ServerName = server.DisplayName,
                    Endpoint = endpoint,
                    Model = model,
                    RawJson = raw,
                    RenderBackend = resolvedProvider,
                    BackendLogLine = backendLogLine,
                    UsesGraphicsBackend = usesGraphicsBackend,
                    ArtifactId = artifactId,
                    ArtifactUrl = artifactId.Length == 0 ? "" : endpoint.TrimEnd('/') + "/api/jackonnx/artifacts/" + Uri.EscapeDataString(artifactId)
                };
            }
            catch (Exception ex)
            {
                string message = FirstNonEmpty(ex.Message, "Generation failed.");
                return new SocketJackComResult
                {
                    Success = false,
                    Text = AppendBackendLogLines("Generation request failed.", backendSummary, backendWarning),
                    Error = AppendBackendLogLines(message, backendSummary, backendWarning),
                    ServerName = server.DisplayName,
                    Endpoint = endpoint,
                    Model = model,
                    RenderBackend = resolvedProvider,
                    BackendLogLine = backendLogLine,
                    UsesGraphicsBackend = usesGraphicsBackend
                };
            }
        }

        private static object BuildOpenAiUserContent(SocketJackComRequest request)
        {
            string prompt = FirstNonEmpty(request.Prompt, "Describe this.");
            string image = FirstNonEmpty(request.ImageDataUrl, request.ImageUrl);
            if (image.Length == 0)
                return prompt;

            return new object[]
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt },
                new Dictionary<string, object>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object> { ["url"] = image }
                }
            };
        }

        private static object BuildSocketJackChatUiUserContent(SocketJackComRequest request)
        {
            object content = BuildOpenAiUserContent(request);
            string text = content as string;
            if (text == null)
                return content;
            return AddNoThinkDirective(text);
        }

        private static string AddNoThinkDirective(string prompt)
        {
            prompt = prompt ?? "";
            string directive = "Do not write a thought process, analysis, or <think> block. Start with the final answer immediately.";
            if (prompt.IndexOf("/no_think", StringComparison.OrdinalIgnoreCase) >= 0)
                return prompt.IndexOf(directive, StringComparison.OrdinalIgnoreCase) >= 0
                    ? prompt
                    : prompt.TrimEnd() + Environment.NewLine + directive;
            return prompt.TrimEnd() + Environment.NewLine + "/no_think" + Environment.NewLine + directive;
        }

        private async Task<string> DetectJackonnxProviderAsync(string endpoint, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return "";

            try
            {
                string statusRaw = await GetJsonAsync(endpoint.TrimEnd('/') + "/api/jackonnx/status", cancellationToken).ConfigureAwait(false);
                return ExtractExecutionProvider(statusRaw);
            }
            catch
            {
                return "";
            }
        }

        private static string FormatBackendSummary(string provider)
        {
            string cleanProvider = NormalizeProvider(provider);
            if (cleanProvider.Length == 0)
                return "JackONNX execution provider: unknown (status not reported)";
            return "JackONNX execution provider: " + ProviderDisplayName(cleanProvider);
        }

        private static string BackendWarning(string provider)
        {
            string cleanProvider = NormalizeProvider(provider);
            if (UsesGpuBackend(cleanProvider))
                return "";
            if (cleanProvider.Length == 0)
                return "[Warning] JackONNX execution provider could not be resolved; this render may be using CPU or another non-graphics backend.";
            return "[Warning] Non-GPU runtime detected (" + ProviderDisplayName(cleanProvider) + "). This render may be on CPU.";
        }

        private static string AppendBackendLogLines(string message, string backendSummary, string warning)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(backendSummary))
                parts.Add(backendSummary);
            if (!string.IsNullOrWhiteSpace(warning))
                parts.Add(warning);
            if (!string.IsNullOrWhiteSpace(message))
                parts.Add(message);
            return string.Join(Environment.NewLine, parts);
        }

        private static string ExtractExecutionProvider(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                    return ExtractExecutionProvider(document.RootElement);
            }
            catch
            {
                return "";
            }
        }

        private static string ExtractExecutionProvider(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyValueString(element, "executionProvider", out string provider) && provider.Length > 0)
                    return provider;
                if (TryGetPropertyValueString(element, "executionprovider", out provider) && provider.Length > 0)
                    return provider;
                if (TryGetPropertyValueString(element, "backend", out provider) && provider.Length > 0)
                    return provider;
                if (TryGetPropertyValueString(element, "backendName", out provider) && provider.Length > 0)
                    return provider;
                if (TryGetPropertyValueString(element, "provider", out provider) && provider.Length > 0)
                    return provider;
                if (TryGetPropertyValueString(element, "runtime", out provider) && provider.Length > 0)
                    return provider;
                if (TryGetPropertyValueString(element, "executionMode", out provider) && provider.Length > 0)
                    return provider;

                if (TryGetPropertyValueProviderArrayChoice(element, "executionProviders", out provider) && provider.Length > 0)
                    return provider;
                if (TryGetPropertyValueProviderArrayChoice(element, "providers", out provider) && provider.Length > 0)
                    return provider;
                if (TryGetPropertyValueProviderArrayChoice(element, "supportedProviders", out provider) && provider.Length > 0)
                    return provider;

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string nested = ExtractExecutionProvider(property.Value);
                    if (nested.Length > 0)
                        return nested;
                }
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    string nested = ExtractExecutionProvider(child);
                    if (nested.Length > 0)
                        return nested;
                }
            }

            return "";
        }

        private static bool TryGetPropertyValueString(JsonElement element, string propertyName, out string value)
        {
            value = "";
            if (!TryGetProperty(element, propertyName, out JsonElement propertyValue))
                return false;

            if (propertyValue.ValueKind == JsonValueKind.String)
            {
                value = propertyValue.GetString() ?? "";
                return value.Length > 0;
            }

            if (propertyValue.ValueKind == JsonValueKind.Number || propertyValue.ValueKind == JsonValueKind.True || propertyValue.ValueKind == JsonValueKind.False)
            {
                value = propertyValue.ToString();
                return value.Length > 0;
            }

            return false;
        }

        private static bool TryGetPropertyValueProviderArrayChoice(JsonElement element, string propertyName, out string provider)
        {
            provider = "";
            if (!TryGetProperty(element, propertyName, out JsonElement providers) || providers.ValueKind != JsonValueKind.Array)
                return false;

            string first = "";
            string graphics = "";
            foreach (JsonElement child in providers.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.String)
                {
                    string candidate = child.GetString() ?? "";
                    if (candidate.Length == 0)
                        continue;
                    if (first.Length == 0)
                        first = candidate;
                    if (graphics.Length == 0 && UsesGpuBackend(NormalizeProvider(candidate)))
                        graphics = candidate;
                }
            }

            provider = graphics.Length > 0 ? graphics : first;
            return provider.Length > 0;
        }

        private static string NormalizeProvider(string provider)
        {
            return (provider ?? "").Trim();
        }

        private static string ProviderDisplayName(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
                return "Unknown";

            string normalized = provider.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? "Unknown" : normalized;
        }

        private static bool UsesGpuBackend(string provider)
        {
            string normalized = NormalizeProvider(provider).ToLowerInvariant();
            if (normalized.Length == 0)
                return false;

            return normalized.Contains("cuda")
                || normalized.Contains("directml")
                || normalized.Contains("trt")
                || normalized.Contains("tensorrt")
                || normalized.Contains("vulkan")
                || normalized.Contains("dml")
                || normalized.Contains("metal")
                || normalized.Contains("rocm")
                || normalized.Contains("opencl")
                || normalized.Contains("hip")
                || normalized.Contains("d3d");
        }

        private async Task<string> PostJsonAsync(string url, object body, CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(body, JsonOptions);
            using (var request = BuildHttpRequest(HttpMethod.Post, url))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + ": " + raw);
                    return raw;
                }
            }
        }

        private static string FormatBackendProbeCommandLine(string endpoint)
        {
            endpoint = NormalizeBaseUrl(endpoint);
            if (endpoint.Length == 0)
                return "[Backend probe] GET /api/jackonnx/status";
            return "[Backend probe] GET " + endpoint.TrimEnd('/') + "/api/jackonnx/status";
        }

        private async Task<string> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            using (var request = BuildHttpRequest(HttpMethod.Get, url))
            {
                using (HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + ": " + raw);
                    return raw;
                }
            }
        }

        private async Task<string> ResolveFirstLoadedModeModelAsync(string endpoint, string modeOrFeature, CancellationToken cancellationToken)
        {
            endpoint = NormalizeBaseUrl(endpoint);
            if (endpoint.Length == 0)
                return "";

            string feature = FeatureForMode(modeOrFeature);
            if (feature.Length == 0)
                return "";

            string loadedModel = await ResolveFirstLoadedModeModelFromPathAsync(endpoint, "/api/models", feature, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(loadedModel))
                return loadedModel;

            loadedModel = await ResolveFirstLoadedModeModelFromPathAsync(endpoint, "/api/model-runtime/models", feature, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(loadedModel))
                return loadedModel;

            string healthModel = await ResolveHealthModeModelAsync(endpoint, feature, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(healthModel) && !IsPlaceholderModelId(healthModel))
                return healthModel;

            return "";
        }

        private async Task<string> ResolveFirstLoadedModeModelFromPathAsync(string endpoint, string path, string feature, CancellationToken cancellationToken)
        {
            try
            {
                string raw = await GetJsonAsync(endpoint.TrimEnd('/') + path, cancellationToken).ConfigureAwait(false);
                return FirstLoadedModeModelFromJson(raw, feature);
            }
            catch
            {
                return "";
            }
        }

        private async Task<string> ResolveHealthModeModelAsync(string endpoint, string feature, CancellationToken cancellationToken)
        {
            try
            {
                string raw = await GetJsonAsync(endpoint.TrimEnd('/') + "/api/health", cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                    return "";

                using (JsonDocument document = JsonDocument.Parse(raw))
                {
                    JsonElement root = document.RootElement;
                    if (string.Equals(feature, SocketJackComFeatures.Chat, StringComparison.OrdinalIgnoreCase))
                    {
                        return FirstNonEmpty(
                            ReadString(root, "chatModel"),
                            ReadString(root, "selectedModel"),
                            ReadString(root, "model"));
                    }
                    if (string.Equals(feature, SocketJackComFeatures.ImageGeneration, StringComparison.OrdinalIgnoreCase))
                        return FirstNonEmpty(ReadString(root, "imageModel"), ReadString(root, "imageGenerationModel"), ReadString(root, "generationModel"));
                    if (string.Equals(feature, SocketJackComFeatures.AudioGeneration, StringComparison.OrdinalIgnoreCase))
                        return FirstNonEmpty(ReadString(root, "audioModel"), ReadString(root, "audioGenerationModel"), ReadString(root, "generationModel"));
                    if (string.Equals(feature, SocketJackComFeatures.VideoGeneration, StringComparison.OrdinalIgnoreCase))
                        return FirstNonEmpty(ReadString(root, "videoModel"), ReadString(root, "videoGenerationModel"), ReadString(root, "generationModel"));
                }
            }
            catch
            {
            }

            return "";
        }

        private HttpRequestMessage BuildHttpRequest(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrWhiteSpace(_options.AuthorizationBearerToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AuthorizationBearerToken.Trim());
            request.Headers.UserAgent.ParseAdd("SocketJack.Com/1.0");
            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)));
                return await SharedHttpClient.SendAsync(request, completionOption, timeout.Token).ConfigureAwait(false);
            }
        }

        private void AddConfiguredDefaults()
        {
            AddCsv(_options.MasterListUrls, Environment.GetEnvironmentVariable("SOCKETJACK_COM_MASTER_LIST"));
            AddCsv(_options.DirectEndpoints, Environment.GetEnvironmentVariable("SOCKETJACK_COM_ENDPOINTS"));

            AddIfMissing(_options.MasterListUrls, "https://socketjack.com/api/lmvsproxy/servers");
            AddIfMissing(_options.MasterListUrls, "https://socketjack.com/api/socketjack-com/servers");
            AddIfMissing(_options.MasterListUrls, "https://JackCast.Live/api/lmvsproxy/servers");
            AddIfMissing(_options.MasterListUrls, "https://JackCast.Live/api/socketjack-com/servers");

            string model = Environment.GetEnvironmentVariable("SOCKETJACK_COM_MODEL");
            if (string.IsNullOrWhiteSpace(_options.DefaultModel) && !string.IsNullOrWhiteSpace(model))
                _options.DefaultModel = model.Trim();

            string token = Environment.GetEnvironmentVariable("SOCKETJACK_COM_BEARER_TOKEN");
            if (string.IsNullOrWhiteSpace(_options.AuthorizationBearerToken) && !string.IsNullOrWhiteSpace(token))
                _options.AuthorizationBearerToken = token.Trim();
        }

        private static void AddCsv(List<string> target, string value)
        {
            if (target == null || string.IsNullOrWhiteSpace(value))
                return;
            foreach (string part in value.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                AddIfMissing(target, part.Trim());
        }

        private static void AddIfMissing(List<string> target, string value)
        {
            if (target == null || string.IsNullOrWhiteSpace(value))
                return;
            if (!target.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
                target.Add(value);
        }

        private static bool TryReserveServerIdentity(HashSet<string> seen, SocketJackComServer server)
        {
            if (seen == null || server == null)
                return false;

            string[] keys =
            {
                server.ServerId,
                server.PublicServerId,
                NormalizeBaseUrl(server.Endpoint),
                NormalizeBaseUrl(server.OpenAiBaseUrl)
            };

            var normalizedKeys = keys
                .Select(key => (key ?? "").Trim())
                .Where(key => key.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedKeys.Count == 0 && !string.IsNullOrWhiteSpace(server.DisplayName))
                normalizedKeys.Add(server.DisplayName.Trim());
            if (normalizedKeys.Count == 0)
                return false;

            if (normalizedKeys.Any(key => seen.Contains(key)))
                return false;

            foreach (string key in normalizedKeys)
                seen.Add(key);
            return true;
        }

        private static IEnumerable<SocketJackComServer> ParseServerList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                yield break;

            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    TryGetProperty(root, "servers", out JsonElement servers) &&
                    servers.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in servers.EnumerateArray())
                        yield return ParseServer(item);
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in root.EnumerateArray())
                        yield return ParseServer(item);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    yield return ParseServer(root);
                }
            }
        }

        private static string FirstLoadedModeModelFromJson(string raw, string feature)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(feature))
                return "";

            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                {
                    foreach (JsonElement model in EnumerateRuntimeModels(document.RootElement))
                    {
                        if (model.ValueKind != JsonValueKind.Object)
                            continue;
                        if (!RuntimeModelIsLoaded(model) || !RuntimeModelSupportsFeature(model, feature))
                            continue;

                        string modelId = RuntimeModelId(model);
                        if (!string.IsNullOrWhiteSpace(modelId))
                            return modelId.Trim();
                    }
                }
            }
            catch
            {
            }

            return "";
        }

        private static IEnumerable<JsonElement> EnumerateRuntimeModels(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                    yield return item;
                yield break;
            }

            if (element.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (string name in new[] { "models", "Models", "data", "Data", "items", "Items" })
            {
                if (TryGetProperty(element, name, out JsonElement rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in rows.EnumerateArray())
                        yield return item;
                    yield break;
                }
            }

            yield return element;
        }

        private static bool RuntimeModelIsLoaded(JsonElement model)
        {
            if (TryGetProperty(model, "loaded_instances", out JsonElement loaded) ||
                TryGetProperty(model, "loadedInstances", out loaded) ||
                TryGetProperty(model, "loaded_models", out loaded) ||
                TryGetProperty(model, "loadedModels", out loaded))
            {
                if (loaded.ValueKind == JsonValueKind.Array)
                    return loaded.GetArrayLength() > 0;
                if (loaded.ValueKind == JsonValueKind.Object || loaded.ValueKind == JsonValueKind.True)
                    return true;
                if (loaded.ValueKind == JsonValueKind.String)
                    return !string.IsNullOrWhiteSpace(loaded.GetString());
            }

            foreach (string name in new[] { "loaded_instance_count", "loadedInstanceCount", "loaded_count", "loadedCount" })
            {
                if (!TryGetProperty(model, name, out JsonElement countValue))
                    continue;
                if (countValue.ValueKind == JsonValueKind.Number && countValue.TryGetInt32(out int count))
                    return count > 0;
                if (countValue.ValueKind == JsonValueKind.String &&
                    int.TryParse(countValue.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                    return count > 0;
            }

            return ReadBool(model, "loaded", false) ||
                   ReadBool(model, "is_loaded", false) ||
                   ReadBool(model, "isLoaded", false) ||
                   ReadBool(model, "active", false) ||
                   ReadBool(model, "ready", false);
        }

        private static bool RuntimeModelSupportsFeature(JsonElement model, string feature)
        {
            string type = ReadString(model, "type");
            string service = FirstNonEmpty(ReadString(model, "chat_service"), ReadString(model, "chatService"), ReadString(model, "service"));

            if (string.Equals(feature, SocketJackComFeatures.Chat, StringComparison.OrdinalIgnoreCase))
            {
                if (ReadBool(model, "chat_completion", false) ||
                    ReadBool(model, "chatCompletion", false) ||
                    ReadBool(model, "supportsChat", false) ||
                    ReadBool(model, "chat", false))
                    return true;
                if (TryGetProperty(model, "capabilities", out JsonElement capabilities) && capabilities.ValueKind == JsonValueKind.Object)
                {
                    if (ReadBool(capabilities, "chat_completion", false) ||
                        ReadBool(capabilities, "chatCompletion", false) ||
                        ReadBool(capabilities, "supportsChat", false) ||
                        ReadBool(capabilities, "chat", false))
                        return true;
                }
                return string.IsNullOrWhiteSpace(service) &&
                       (string.IsNullOrWhiteSpace(type) ||
                        string.Equals(type, "llm", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "chat", StringComparison.OrdinalIgnoreCase));
            }

            string mediaKind = feature == SocketJackComFeatures.ImageGeneration ? "image" :
                               feature == SocketJackComFeatures.AudioGeneration ? "audio" :
                               feature == SocketJackComFeatures.VideoGeneration ? "video" : "";
            if (mediaKind.Length == 0)
                return false;

            if (string.Equals(type, mediaKind, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(service, mediaKind + "_generation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(service, feature, StringComparison.OrdinalIgnoreCase))
                return true;

            if (TryGetProperty(model, "capabilities", out JsonElement caps) && caps.ValueKind == JsonValueKind.Object)
            {
                return ReadBool(caps, mediaKind + "_generation", false) ||
                       ReadBool(caps, mediaKind + "Generation", false) ||
                       ReadBool(caps, "generation", false);
            }

            return false;
        }

        private static string RuntimeModelId(JsonElement model)
        {
            return FirstNonEmpty(
                ReadString(model, "key"),
                ReadString(model, "id"),
                ReadString(model, "model"),
                ReadString(model, "modelId"),
                ReadString(model, "name"),
                ReadString(model, "display_name"),
                ReadString(model, "displayName"));
        }

        private static SocketJackComServer ParseServer(JsonElement element)
        {
            JsonElement profile = element;
            if (TryGetProperty(element, "profile", out JsonElement profileElement) &&
                profileElement.ValueKind == JsonValueKind.Object)
                profile = profileElement;

            string endpoint = NormalizeBaseUrl(FirstNonEmpty(
                ReadString(element, "endpoint"),
                ReadString(element, "launchUrl"),
                ReadString(profile, "publicUrl"),
                ReadString(profile, "endpoint"),
                ReadString(profile, "launchUrl")));
            string openAiBaseUrl = NormalizeBaseUrl(FirstNonEmpty(
                ReadString(element, "openAiBaseUrl"),
                ReadString(element, "openAiEndpoint"),
                ReadString(element, "vsProxyEndpoint"),
                ReadString(element, "copilotEndpoint"),
                ReadString(element, "proxyEndpoint"),
                ReadString(profile, "openAiBaseUrl"),
                ReadString(profile, "openAiEndpoint"),
                ReadString(profile, "vsProxyEndpoint"),
                ReadString(profile, "copilotEndpoint"),
                ReadString(profile, "proxyEndpoint")));

            if (openAiBaseUrl.Length == 0)
                openAiBaseUrl = BuildOpenAiBaseUrlFromMasterEntry(element, profile, endpoint);

            var server = new SocketJackComServer
            {
                ServerId = FirstNonEmpty(ReadString(element, "serverId"), ReadString(element, "id"), ReadString(profile, "serverId"), ReadString(profile, "id")),
                Name = FirstNonEmpty(ReadString(element, "serverName"), ReadString(element, "displayName"), ReadString(element, "title"), ReadString(element, "name"), ReadString(profile, "serverName"), ReadString(profile, "title")),
                OwnerUserName = FirstNonEmpty(ReadString(element, "ownerUserName"), ReadString(profile, "ownerUserName")),
                Endpoint = endpoint,
                OpenAiBaseUrl = openAiBaseUrl,
                SelectedModel = FirstNonEmpty(ReadString(element, "selectedModel"), ReadString(element, "model"), ReadString(profile, "selectedModel"), ReadString(profile, "model"), FirstModel(FirstNonEmpty(ReadString(element, "availableModels"), ReadString(profile, "availableModels")))),
                AvailableModels = FirstNonEmpty(ReadString(element, "availableModels"), ReadString(profile, "availableModels")),
                ToolsAllowed = FirstNonEmpty(ReadString(element, "toolsAllowed"), ReadString(profile, "toolsAllowed")),
                HardwareSummary = FirstNonEmpty(ReadString(element, "hardwareSummary"), ReadString(element, "availableResources"), ReadString(profile, "availableResources")),
                AvailableRam = FirstNonEmpty(ReadString(element, "availableRam"), ReadString(element, "ramAvailable"), ReadString(profile, "availableRam"), ReadString(profile, "ramAvailable")),
                AvailableVram = FirstNonEmpty(ReadString(element, "availableVram"), ReadString(element, "vramAvailable"), ReadString(element, "gpuAvailable"), ReadString(profile, "availableVram"), ReadString(profile, "vramAvailable"), ReadString(profile, "gpuAvailable")),
                GpuModel = FirstNonEmpty(ReadString(element, "gpuModel"), ReadString(element, "gpuName"), ReadString(profile, "gpuModel"), ReadString(profile, "gpuName")),
                CpuModel = FirstNonEmpty(ReadString(element, "cpuModel"), ReadString(element, "cpuName"), ReadString(profile, "cpuModel"), ReadString(profile, "cpuName")),
                MaxTokens = ReadLong(element, "maxTokens", ReadLong(profile, "maxTokens", 0)),
                Online = ReadBool(element, "online", ReadBool(element, "isOnline", ReadBool(element, "hostResponding", ReadBool(profile, "online", ReadBool(profile, "isOnline", true))))),
                HealthScore = ReadInt(element, "healthScore", ReadNestedInt(element, "monitoring", "healthScore", ReadNestedInt(profile, "monitoring", "healthScore", 50))),
                SourceJson = element.GetRawText()
            };

            AddCapabilities(server, element);
            AddCapabilities(server, profile);
            AddToolTokens(server, server.ToolsAllowed);
            AddToolTokens(server, server.AvailableModels);
            InferFeatures(server);
            return server;
        }

        private static string BuildOpenAiBaseUrlFromMasterEntry(JsonElement element, JsonElement profile, string endpoint)
        {
            if (IsProxyEndpoint(endpoint))
                return NormalizeBaseUrl(endpoint);

            int proxyPort = ReadInt(element, "proxyPort", ReadInt(element, "vsProxyPort", ReadNestedInt(element, "ports", "vsProxy", ReadInt(profile, "proxyPort", ReadInt(profile, "vsProxyPort", ReadNestedInt(profile, "ports", "vsProxy", 0))))));
            if (proxyPort <= 0 || proxyPort > 65535)
                return "";

            string host = FirstNonEmpty(
                ReadString(element, "connectHost"),
                ReadString(element, "host"),
                ReadString(profile, "connectHost"),
                ReadString(profile, "host"),
                HostFromUrl(endpoint));

            if (string.IsNullOrWhiteSpace(host))
                return "";

            string scheme = SchemeFromUrl(endpoint);
            if (scheme.Length == 0)
                scheme = "http";

            return NormalizeBaseUrl(scheme + "://" + host.Trim().TrimEnd('/') + ":" + proxyPort.ToString(CultureInfo.InvariantCulture));
        }

        private static bool IsProxyEndpoint(string value)
        {
            value = NormalizeUrl(value);
            return value.Length > 0 &&
                   Uri.TryCreate(value, UriKind.Absolute, out Uri uri) &&
                   uri.AbsolutePath.StartsWith("/proxy/", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddCapabilities(SocketJackComServer server, JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Object)
                return;

            AddCapabilityFlags(server, source);
            if (TryGetProperty(source, "capabilities", out JsonElement capabilities))
                AddCapabilityFlags(server, capabilities);
            if (TryGetProperty(source, "models", out JsonElement models))
                AddModelCapabilities(server, models);
            if (TryGetProperty(source, "modelCapabilities", out JsonElement modelCapabilities))
                AddModelCapabilities(server, modelCapabilities);
            AddModelCapabilitiesFromJson(server, ReadString(source, "modelCapabilitiesJson"));
            AddModelCapabilitiesFromJson(server, ReadString(source, "modelBenchmarksJson"));
        }

        private static void AddCapabilityFlags(SocketJackComServer server, JsonElement item)
        {
            if (ReadBool(item, "vision", false) || ReadBool(item, "supportsVision", false) || ReadBool(item, "images", false) || ReadBool(item, "supportsImages", false))
            {
                server.Features.Add(SocketJackComFeatures.Vision);
                server.Features.Add(SocketJackComFeatures.ImageAnalysis);
                server.Features.Add(SocketJackComFeatures.VideoAnalysis);
            }
            if (ReadBool(item, "tools", false) || ReadBool(item, "supportsTools", false))
            {
                server.Features.Add(SocketJackComFeatures.Tools);
            }
            if (ReadBool(item, "imageGeneration", false) || ReadBool(item, "supportsImageGeneration", false) || ReadBool(item, "generatesImages", false))
                server.Features.Add(SocketJackComFeatures.ImageGeneration);
            if (ReadBool(item, "audioGeneration", false) || ReadBool(item, "supportsAudioGeneration", false) || ReadBool(item, "generatesAudio", false))
                server.Features.Add(SocketJackComFeatures.AudioGeneration);
            if (ReadBool(item, "videoGeneration", false) || ReadBool(item, "supportsVideoGeneration", false) || ReadBool(item, "generatesVideo", false))
                server.Features.Add(SocketJackComFeatures.VideoGeneration);

            string text = string.Join(" ", new[]
            {
                ReadString(item, "toolsAllowed"),
                ReadString(item, "tools"),
                ReadString(item, "availableResources"),
                ReadString(item, "description"),
                ReadString(item, "statusText"),
                ReadString(item, "lastStatus"),
                ReadString(item, "searchText")
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (ContainsAny(text, "JackONNX media", "jackonnx.media", "jackonnx models", "jackonnx_models_list"))
            {
                server.Features.Add(SocketJackComFeatures.ImageGeneration);
                server.Features.Add(SocketJackComFeatures.AudioGeneration);
                server.Features.Add(SocketJackComFeatures.VideoGeneration);
            }
            if (ContainsAny(text, "jackonnx_image_generate", "image.generate", "image-generation", "image generation", "generate image"))
                server.Features.Add(SocketJackComFeatures.ImageGeneration);
            if (ContainsAny(text, "jackonnx_audio_speech", "audio.generate", "audio-generation", "audio generation", "text to speech", "tts"))
                server.Features.Add(SocketJackComFeatures.AudioGeneration);
            if (ContainsAny(text, "jackonnx_video_generate", "video.generate", "video-generation", "video generation", "generate video"))
                server.Features.Add(SocketJackComFeatures.VideoGeneration);
        }

        private static void AddModelCapabilities(SocketJackComServer server, JsonElement models)
        {
            if (models.ValueKind == JsonValueKind.Object)
            {
                foreach (string name in new[] { "models", "Models", "data", "Data", "items", "Items" })
                {
                    if (TryGetProperty(models, name, out JsonElement nested) && nested.ValueKind == JsonValueKind.Array)
                    {
                        AddModelCapabilities(server, nested);
                        return;
                    }
                }
            }

            if (models.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement model in models.EnumerateArray())
            {
                if (model.ValueKind == JsonValueKind.String)
                {
                    AddToolTokens(server, model.GetString());
                    continue;
                }
                AddCapabilityFlags(server, model);
                if (TryGetProperty(model, "capabilities", out JsonElement capabilities))
                    AddCapabilityFlags(server, capabilities);
                string id = FirstNonEmpty(ReadString(model, "id"), ReadString(model, "modelId"), ReadString(model, "name"), ReadString(model, "displayName"));
                AddToolTokens(server, id);
                if (server.SelectedModel.Length == 0)
                    server.SelectedModel = id;
                RegisterModelMode(server, id, model);
                if (TryGetProperty(model, "capabilities", out JsonElement modeCapabilities))
                    RegisterModelMode(server, id, modeCapabilities);
            }
        }

        private static void AddModelCapabilitiesFromJson(SocketJackComServer server, string json)
        {
            if (server == null || string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    AddCapabilities(server, document.RootElement);
                    AddCapabilityFlags(server, document.RootElement);
                    AddModelCapabilities(server, document.RootElement);
                }
            }
            catch
            {
            }
        }

        private static void AddToolTokens(SocketJackComServer server, string value)
        {
            if (server == null || string.IsNullOrWhiteSpace(value))
                return;

            string[] tokens = value.Split(new[] { ',', ';', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in tokens)
            {
                string token = raw.Trim().Trim('"', '\'').ToLowerInvariant();
                if (token.Length == 0)
                    continue;
                server.Tools.Add(token);
            }
        }

        private static void InferFeatures(SocketJackComServer server)
        {
            if (server == null)
                return;

            server.Features.Add(SocketJackComFeatures.Chat);
            if (server.Tools.Count > 0)
                server.Features.Add(SocketJackComFeatures.Tools);

            string advertisedText = string.Join(" ", new[] { server.ToolsAllowed, server.HardwareSummary, server.SourceJson }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (ContainsAny(advertisedText, "JackONNX media", "jackonnx.media", "jackonnx models", "jackonnx_models_list"))
            {
                server.Features.Add(SocketJackComFeatures.ImageGeneration);
                server.Features.Add(SocketJackComFeatures.AudioGeneration);
                server.Features.Add(SocketJackComFeatures.VideoGeneration);
            }

            foreach (string tool in server.Tools)
            {
                if (ContainsAny(tool, "vision", "vlm", "image", "multimodal"))
                {
                    server.Features.Add(SocketJackComFeatures.Vision);
                    server.Features.Add(SocketJackComFeatures.ImageAnalysis);
                    server.Features.Add(SocketJackComFeatures.VideoAnalysis);
                }
                if (ContainsAny(tool, "jackonnx_image_generate", "image.generate", "image-generation", "generate-image"))
                    server.Features.Add(SocketJackComFeatures.ImageGeneration);
                if (ContainsAny(tool, "jackonnx_audio_speech", "audio.generate", "audio-generation", "tts", "speech"))
                    server.Features.Add(SocketJackComFeatures.AudioGeneration);
                if (ContainsAny(tool, "jackonnx_video_generate", "video.generate", "video-generation", "generate-video"))
                    server.Features.Add(SocketJackComFeatures.VideoGeneration);
            }
        }

        private static void RegisterModelMode(SocketJackComServer server, string modelId, JsonElement source)
        {
            if (server == null || string.IsNullOrWhiteSpace(modelId) || source.ValueKind != JsonValueKind.Object)
                return;

            if (ReadBool(source, "chat", false) || ReadBool(source, "supportsChat", false) || ReadBool(source, "llm", false))
                SetModeModel(server, SocketJackComFeatures.Chat, modelId);
            if (ReadBool(source, "imageGeneration", false) || ReadBool(source, "supportsImageGeneration", false) || ReadBool(source, "generatesImages", false))
                SetModeModel(server, SocketJackComFeatures.ImageGeneration, modelId);
            if (ReadBool(source, "audioGeneration", false) || ReadBool(source, "supportsAudioGeneration", false) || ReadBool(source, "generatesAudio", false))
                SetModeModel(server, SocketJackComFeatures.AudioGeneration, modelId);
            if (ReadBool(source, "videoGeneration", false) || ReadBool(source, "supportsVideoGeneration", false) || ReadBool(source, "generatesVideo", false))
                SetModeModel(server, SocketJackComFeatures.VideoGeneration, modelId);

            string text = string.Join(" ", new[] { modelId, ReadString(source, "type"), ReadString(source, "kind"), ReadString(source, "tags"), ReadString(source, "capabilities") }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (ContainsAny(text, "image", "diffusion", "sdxl", "stable-diffusion"))
                SetModeModel(server, SocketJackComFeatures.ImageGeneration, modelId);
            if (ContainsAny(text, "audio", "speech", "voice", "tts"))
                SetModeModel(server, SocketJackComFeatures.AudioGeneration, modelId);
            if (ContainsAny(text, "video", "movie", "clip"))
                SetModeModel(server, SocketJackComFeatures.VideoGeneration, modelId);
        }

        private static void SetModeModel(SocketJackComServer server, string feature, string modelId)
        {
            if (server == null || string.IsNullOrWhiteSpace(feature) || string.IsNullOrWhiteSpace(modelId))
                return;
            if (!server.ModeModels.ContainsKey(feature))
                server.ModeModels[feature] = modelId.Trim();
        }

        private static bool Matches(SocketJackComServer server, SocketJackComRequirements requirements)
        {
            if (server == null || requirements == null || !server.Online)
                return false;

            foreach (string feature in requirements.RequiredFeatures())
            {
                if (!server.Features.Contains(feature))
                    return false;
            }

            foreach (string requiredTool in requirements.RequiredTools ?? new List<string>())
            {
                if (!server.Tools.Contains((requiredTool ?? "").Trim().ToLowerInvariant()))
                    return false;
            }

            return true;
        }

        private static bool ServerIdMatches(SocketJackComServer server, string preferredServerId)
        {
            if (server == null || string.IsNullOrWhiteSpace(preferredServerId))
                return false;

            preferredServerId = preferredServerId.Trim();
            return string.Equals(server.PublicServerId, preferredServerId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(server.ServerId, preferredServerId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(server.DisplayName, preferredServerId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(server.Endpoint, preferredServerId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(server.OpenAiBaseUrl, preferredServerId, StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractOpenAiText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                {
                    JsonElement root = document.RootElement;
                    if (TryGetProperty(root, "choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement choice in choices.EnumerateArray())
                        {
                            if (TryGetProperty(choice, "message", out JsonElement message) &&
                                TryGetProperty(message, "content", out JsonElement content))
                                return JsonText(content);
                            if (TryGetProperty(choice, "text", out JsonElement text))
                                return JsonText(text);
                        }
                    }
                    return ExtractSocketJackChatUiText(raw);
                }
            }
            catch
            {
                return "";
            }
        }

        private static string ExtractSocketJackChatUiText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                {
                    JsonElement root = document.RootElement;
                    return FirstNonEmpty(
                        ReadString(root, "content"),
                        ReadString(root, "text"),
                        ReadString(root, "message"),
                        ReadString(root, "response"),
                        ReadString(root, "output"));
                }
            }
            catch
            {
                return "";
            }
        }

        private static string NormalizeStreamEventLine(string line)
        {
            string trimmed = (line ?? "").Trim();
            if (trimmed.Length == 0)
                return "";
            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(5).Trim();
            if (trimmed.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                return "{\"type\":\"done\"}";
            return trimmed.StartsWith("{", StringComparison.Ordinal) ? trimmed : "";
        }

        private static string ExtractChatUiStreamContent(JsonElement root)
        {
            string direct = FirstNonEmptyPreserveWhitespace(
                ReadString(root, "content"),
                ReadString(root, "text"),
                ReadString(root, "response"),
                ReadString(root, "output"));
            if (direct.Length > 0)
                return direct;

            if (TryGetProperty(root, "delta", out JsonElement delta))
            {
                string value = FirstNonEmptyPreserveWhitespace(ReadString(delta, "content"), ReadString(delta, "text"));
                if (value.Length > 0)
                    return value;
            }

            if (TryGetProperty(root, "data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
            {
                string value = ExtractChatUiStreamContent(data);
                if (value.Length > 0)
                    return value;
            }

            if (TryGetProperty(root, "choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement choice in choices.EnumerateArray())
                {
                    if (TryGetProperty(choice, "delta", out JsonElement choiceDelta))
                    {
                        string value = FirstNonEmptyPreserveWhitespace(ReadString(choiceDelta, "content"), ReadString(choiceDelta, "text"));
                        if (value.Length > 0)
                            return value;
                    }
                    if (TryGetProperty(choice, "message", out JsonElement message))
                    {
                        string value = FirstNonEmptyPreserveWhitespace(ReadString(message, "content"), ReadString(message, "text"));
                        if (value.Length > 0)
                            return value;
                    }
                    string text = ReadString(choice, "text");
                    if (text.Length > 0)
                        return text;
                }
            }

            return "";
        }

        private static string ExtractChatUiStreamReasoning(JsonElement root)
        {
            string direct = FirstNonEmptyPreserveWhitespace(
                ReadString(root, "reasoning"),
                ReadString(root, "reasoning_content"),
                ReadString(root, "reasoningContent"),
                ReadString(root, "reasoning_text"),
                ReadString(root, "reasoningText"));
            if (direct.Length > 0)
                return direct;

            if (TryGetProperty(root, "delta", out JsonElement delta))
            {
                string value = FirstNonEmptyPreserveWhitespace(
                    ReadString(delta, "reasoning"),
                    ReadString(delta, "reasoning_content"),
                    ReadString(delta, "reasoningContent"));
                if (value.Length > 0)
                    return value;
            }

            if (TryGetProperty(root, "data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
            {
                string value = ExtractChatUiStreamReasoning(data);
                if (value.Length > 0)
                    return value;
            }

            if (TryGetProperty(root, "choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement choice in choices.EnumerateArray())
                {
                    if (TryGetProperty(choice, "delta", out JsonElement choiceDelta))
                    {
                        string value = FirstNonEmptyPreserveWhitespace(
                            ReadString(choiceDelta, "reasoning"),
                            ReadString(choiceDelta, "reasoning_content"),
                            ReadString(choiceDelta, "reasoningContent"));
                        if (value.Length > 0)
                            return value;
                    }
                    if (TryGetProperty(choice, "message", out JsonElement message))
                    {
                        string value = FirstNonEmptyPreserveWhitespace(
                            ReadString(message, "reasoning"),
                            ReadString(message, "reasoning_content"),
                            ReadString(message, "reasoningContent"));
                        if (value.Length > 0)
                            return value;
                    }
                }
            }

            return "";
        }

        private static bool IsGenerationSuccess(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                {
                    JsonElement root = document.RootElement;
                    if (TryGetProperty(root, "success", out JsonElement success) && success.ValueKind == JsonValueKind.False)
                        return false;
                    if (TryGetProperty(root, "success", out success) && success.ValueKind == JsonValueKind.True)
                        return true;
                    if (TryGetProperty(root, "Success", out success) && success.ValueKind == JsonValueKind.True)
                        return true;
                    if (TryGetProperty(root, "artifacts", out JsonElement artifacts) && artifacts.ValueKind == JsonValueKind.Array && artifacts.GetArrayLength() > 0)
                        return true;
                    return ReadString(root, "message").Length > 0 && ReadString(root, "error").Length == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractGenerationMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                {
                    JsonElement root = document.RootElement;
                    return FirstNonEmpty(
                        ReadString(root, "message"),
                        ReadString(root, "Message"),
                        ReadString(root, "error"),
                        ReadString(root, "Error"),
                        "Generation request completed.");
                }
            }
            catch
            {
                return "";
            }
        }

        private static string ExtractFirstString(string raw, params string[] propertyNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                    return ExtractFirstString(document.RootElement, propertyNames);
            }
            catch
            {
                return "";
            }
        }

        private static string ExtractPreferredGenerationArtifactId(string raw, string mediaKind)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            try
            {
                using (JsonDocument document = JsonDocument.Parse(raw))
                {
                    string preferred = FindPreferredArtifactId(document.RootElement, mediaKind);
                    if (preferred.Length > 0)
                        return preferred;
                }
            }
            catch
            {
            }
            return ExtractFirstString(raw, "artifactId", "id");
        }

        private static string FindPreferredArtifactId(JsonElement element, string mediaKind)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProperty(element, "artifacts", out JsonElement artifacts) && artifacts.ValueKind == JsonValueKind.Array)
                {
                    string fallback = "";
                    foreach (JsonElement artifact in artifacts.EnumerateArray())
                    {
                        string id = ReadString(artifact, "id");
                        if (id.Length == 0)
                            continue;
                        if (fallback.Length == 0)
                            fallback = id;
                        if (ArtifactIsPreferredForMediaKind(artifact, mediaKind))
                            return id;
                    }
                    if (fallback.Length > 0)
                        return fallback;
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "artifacts", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string nested = FindPreferredArtifactId(property.Value, mediaKind);
                    if (nested.Length > 0)
                        return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    string nested = FindPreferredArtifactId(child, mediaKind);
                    if (nested.Length > 0)
                        return nested;
                }
            }
            return "";
        }

        private static bool ArtifactIsPreferredForMediaKind(JsonElement artifact, string mediaKind)
        {
            if (!string.Equals(mediaKind, "video", StringComparison.OrdinalIgnoreCase))
                return true;

            string mediaType = ReadString(artifact, "mediaType");
            string filePath = ReadString(artifact, "filePath");
            string encoding = "";
            string presentationKind = "";
            if (TryGetProperty(artifact, "metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                encoding = ReadString(metadata, "encoding");
                presentationKind = FirstNonEmpty(
                    ReadString(metadata, "presentationKind"),
                    ReadString(metadata, "presentation"),
                    ReadString(metadata, "browserPlayable"));
            }

            return mediaType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase) &&
                   (presentationKind.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
                    encoding.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
                    filePath.Contains(".presentation.", StringComparison.OrdinalIgnoreCase));
        }

        private static string ExtractFirstString(JsonElement element, params string[] propertyNames)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (string name in propertyNames)
                {
                    if (TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString() ?? "";
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string nested = ExtractFirstString(property.Value, propertyNames);
                    if (nested.Length > 0)
                        return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    string nested = ExtractFirstString(child, propertyNames);
                    if (nested.Length > 0)
                        return nested;
                }
            }
            return "";
        }

        private static string JsonText(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? "";
            if (element.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (JsonElement part in element.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object && TryGetProperty(part, "text", out JsonElement text))
                        parts.Add(JsonText(text));
                    else if (part.ValueKind == JsonValueKind.String)
                        parts.Add(part.GetString());
                }
                return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
            }
            return element.GetRawText();
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            value = default(JsonElement);
            if (element.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(name))
                return false;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            return false;
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (TryGetProperty(element, name, out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
                if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                    return value.ToString();
                if (value.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            parts.Add(item.GetString());
                        else if (item.ValueKind == JsonValueKind.Object)
                            parts.Add(FirstNonEmpty(ReadString(item, "id"), ReadString(item, "modelId"), ReadString(item, "name"), ReadString(item, "displayName")));
                    }
                    return string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
                }
            }
            return "";
        }

        private static bool ReadBool(JsonElement element, string name, bool defaultValue)
        {
            if (TryGetProperty(element, name, out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.True)
                    return true;
                if (value.ValueKind == JsonValueKind.False)
                    return false;
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed))
                    return parsed;
            }
            return defaultValue;
        }

        private static int ReadInt(JsonElement element, string name, int defaultValue)
        {
            if (TryGetProperty(element, name, out JsonElement value))
            {
                if (value.TryGetInt32(out int parsed))
                    return parsed;
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            return defaultValue;
        }

        private static long ReadLong(JsonElement element, string name, long defaultValue)
        {
            if (TryGetProperty(element, name, out JsonElement value))
            {
                if (value.TryGetInt64(out long parsed))
                    return parsed;
                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            return defaultValue;
        }

        private static int ReadNestedInt(JsonElement element, string objectName, string name, int defaultValue)
        {
            if (TryGetProperty(element, objectName, out JsonElement nested))
                return ReadInt(nested, name, defaultValue);
            return defaultValue;
        }

        private static string FirstModeModel(SocketJackComServer server, string modeOrFeature)
        {
            if (server == null)
                return "";

            string feature = FeatureForMode(modeOrFeature);
            if (feature.Length > 0 &&
                server.ModeModels.TryGetValue(feature, out string model) &&
                !string.IsNullOrWhiteSpace(model))
                return model.Trim();

            if (string.Equals(feature, SocketJackComFeatures.Chat, StringComparison.OrdinalIgnoreCase))
                return FirstNonEmpty(server.SelectedModel, FirstModel(server.AvailableModels));

            return "";
        }

        private static string FeatureForMode(string modeOrFeature)
        {
            string value = (modeOrFeature ?? "").Trim().ToLowerInvariant();
            if (value == "chat" || value == SocketJackComFeatures.Chat)
                return SocketJackComFeatures.Chat;
            if (value == "image" || value == SocketJackComFeatures.ImageGeneration)
                return SocketJackComFeatures.ImageGeneration;
            if (value == "audio" || value == SocketJackComFeatures.AudioGeneration)
                return SocketJackComFeatures.AudioGeneration;
            if (value == "video" || value == SocketJackComFeatures.VideoGeneration)
                return SocketJackComFeatures.VideoGeneration;
            return "";
        }

        private static bool IsPlaceholderModelId(string model)
        {
            string value = (model ?? "").Trim().ToLowerInvariant();
            return value.Length == 0 ||
                   value == "lm-studio" ||
                   value == "lmstudio" ||
                   value == "local-model";
        }

        private static string FirstModel(string availableModels)
        {
            if (string.IsNullOrWhiteSpace(availableModels))
                return "";
            string[] parts = availableModels.Split(new[] { ',', ';', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "" : parts[0].Trim();
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            foreach (string needle in needles)
            {
                if (!string.IsNullOrWhiteSpace(needle) &&
                    value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string NormalizeMediaKind(string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            if (value == "img" || value == "picture" || value == "photo")
                return "image";
            if (value == "voice" || value == "speech" || value == "sound")
                return "audio";
            if (value == "movie" || value == "clip")
                return "video";
            return value == "image" || value == "audio" || value == "video" ? value : "";
        }

        private static string NormalizeUrl(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0)
                return "";
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
                return "";
            return uri.ToString();
        }

        private static string HostFromUrl(string value)
        {
            value = NormalizeUrl(value);
            if (value.Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
                return "";
            return uri.Host;
        }

        private static string SchemeFromUrl(string value)
        {
            value = NormalizeUrl(value);
            if (value.Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
                return "";
            return uri.Scheme;
        }

        private static string NormalizeBaseUrl(string value)
        {
            value = NormalizeUrl(value);
            if (value.Length == 0)
                return "";
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
                return "";

            string path = uri.AbsolutePath ?? "";
            string loweredPath = path.ToLowerInvariant();
            int v1Index = loweredPath.IndexOf("/v1/", StringComparison.Ordinal);
            if (v1Index < 0 && loweredPath.EndsWith("/v1", StringComparison.Ordinal))
                v1Index = loweredPath.Length - 3;
            if (v1Index >= 0)
                path = path.Substring(0, v1Index);
            path = path.TrimEnd('/');
            string authority = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            if (path.StartsWith("/proxy/", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(uri.Host, "socketjack.com", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri)
                {
                    Host = "www.socketjack.com",
                    Path = path
                };
                return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            }
            return authority + path;
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

        private static string FirstNonEmptyPreserveWhitespace(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
            return "";
        }

        private static SocketJackComResult Failure(string message)
        {
            return new SocketJackComResult
            {
                Success = false,
                Error = message ?? "",
                Text = ""
            };
        }
    }
}
