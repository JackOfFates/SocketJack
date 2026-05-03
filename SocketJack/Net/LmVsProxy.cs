using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    /// <summary>
    /// LmVsProxy - A protocol bridge between Visual Studio's GitHub-style tool calling
    /// and LM Studio's OpenAI-style API.
    /// 
    /// The proxy:
    /// 1. Accepts VS requests at /v1/responses
    /// 2. Translates tool schemas from GitHub format to OpenAI format
    /// 3. Forwards requests to LM Studio's /v1/chat/completions
    /// 4. Handles multi-turn conversations by re-sending tool results
    /// 5. Converts LM Studio responses back to VS SSE format
    /// </summary>
    public class LmVsProxy : IDisposable {

        public bool IsListening => _isRunning;

        /// <summary>
        /// Port used by the lightweight browser chat UI hosted through SocketJack.HttpServer.
        /// </summary>
        public int ChatServerPort => _chatServerPort;

        /// <summary>
        /// Local URL for the lightweight browser chat UI.
        /// </summary>
        public string ChatServerUrl => $"http://localhost:{_chatServerPort}/";

        /// <summary>
        /// Model id sent by the chat UI to LM Studio's OpenAI-compatible endpoint.
        /// </summary>
        public string ChatModel { get; set; } = "lm-studio";

        /// <summary>
        /// Returns true after the lazy chat server has been created.
        /// </summary>
        public bool IsChatServerCreated => _chatServer != null;

        /// <summary>
        /// Lazily creates a small HttpServer that serves a browser chat UI backed by LM Studio.
        /// Call <see cref="TcpServer.Listen"/> on the returned server to start it.
        /// </summary>
        public HttpServer ChatServer {
            get {
                if (_isDisposed)
                    throw new ObjectDisposedException("LmVsProxy is disposed.");

                if (_chatServer == null) {
                    lock (_chatServerLock) {
                        if (_chatServer == null)
                            _chatServer = CreateChatServer();
                    }
                }

                return _chatServer;
            }
        }

        /// <summary>
        /// Event fired whenever LmVsProxy logs a message.
        /// </summary>
        public event EventHandler<OutputLogEventArgs> OutputLog;

        #region Fields

        private readonly string _lmStudioHost;
        private readonly int _lmStudioPort;
        private readonly int _proxyPort;
        private readonly int _chatServerPort;
        private readonly object _chatServerLock = new object();
        private HttpListener _httpListener;
        private System.Net.Http.HttpClient _httpClient;
        private HttpServer _chatServer;
        private bool _isRunning = false;
        private bool _isDisposed = false;
        private CancellationTokenSource _cts;
        private Task _listenerTask;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new LmVsProxy that listens for VS requests and forwards to LM Studio.
        /// </summary>
        /// <param name="lmStudioHost">The LM Studio host (e.g., "localhost").</param>
        /// <param name="lmStudioPort">The LM Studio port (e.g., 1234).</param>
        /// <param name="proxyPort">The local port to listen on for VS requests.</param>
        /// <param name="chatServerPort">Optional local port for the browser chat UI. When 0, a non-conflicting port is chosen.</param>
        public LmVsProxy(string lmStudioHost, int lmStudioPort, int proxyPort, int chatServerPort = 0) {
            if (string.IsNullOrWhiteSpace(lmStudioHost))
                throw new ArgumentException("LM Studio host is required.", nameof(lmStudioHost));
            if (lmStudioPort <= 0 || lmStudioPort > 65535)
                throw new ArgumentException("LM Studio port must be between 1 and 65535.", nameof(lmStudioPort));
            if (proxyPort <= 0 || proxyPort > 65535)
                throw new ArgumentException("Proxy port must be between 1 and 65535.", nameof(proxyPort));
            if (chatServerPort < 0 || chatServerPort > 65535)
                throw new ArgumentException("Chat server port must be between 1 and 65535, or 0 for automatic selection.", nameof(chatServerPort));

            _lmStudioHost = lmStudioHost;
            _lmStudioPort = lmStudioPort;
            _proxyPort = proxyPort;
            _chatServerPort = chatServerPort == 0
                ? ChooseDefaultChatServerPort(lmStudioPort, proxyPort)
                : chatServerPort;
            _httpClient = new System.Net.Http.HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(10); // Long timeout for streaming
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts the proxy server listening for VS requests.
        /// </summary>
        /// <returns>True if successfully started; false otherwise.</returns>
        public bool Start() {
            if (_isDisposed)
                throw new ObjectDisposedException("LmVsProxy is disposed.");
            if (_isRunning)
                return false;

            try {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{_proxyPort}/");
                _httpListener.Start();

                _cts = new CancellationTokenSource();
                _isRunning = true;

                // Start listener loop on background thread
                _listenerTask = Task.Run(() => ListenerLoop(_cts.Token), _cts.Token);

                return true;
            } catch (Exception ex) {
                throw new InvalidOperationException($"Failed to start LmVsProxy on port {_proxyPort}.", ex);
            }
        }

        /// <summary>
        /// Stops the proxy server.
        /// </summary>
        public void Stop() {
            StopChatServer();

            if (!_isRunning)
                return;

            _isRunning = false;
            _cts?.Cancel();
            try {
                _httpListener?.Stop();
                _listenerTask?.Wait(TimeSpan.FromSeconds(5));
            } catch { }
        }

        /// <summary>
        /// Logs a message through the OutputLog event with formatted brackets.
        /// </summary>
        private void LogMessage(string message) {
            string formatted = "[LmVsProxy] " + (message ?? "");
            if (!formatted.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                formatted += Environment.NewLine;

            OutputLog?.Invoke(this, new OutputLogEventArgs(formatted));
        }

        #endregion

        #region Private Methods

        private static int ChooseDefaultChatServerPort(int lmStudioPort, int proxyPort) {
            int[] candidates = new[] {
                proxyPort + 1,
                proxyPort + 2,
                lmStudioPort + 1,
                11436
            };

            foreach (int candidate in candidates) {
                if (candidate > 0 &&
                    candidate <= 65535 &&
                    candidate != proxyPort &&
                    candidate != lmStudioPort)
                    return candidate;
            }

            for (int candidate = 49152; candidate <= 65535; candidate++) {
                if (candidate != proxyPort && candidate != lmStudioPort)
                    return candidate;
            }

            throw new InvalidOperationException("Unable to choose a chat server port.");
        }

        private HttpServer CreateChatServer() {
            var server = new HttpServer(_chatServerPort, "LmVsProxy Chat UI");
            server.IndexPageHtml = BuildChatUiHtml();
            server.Robots = "User-agent: *\nDisallow: /\n";

            server.Map("GET", "/health", (connection, request, cancellationToken) =>
                JsonSerializer.Serialize(new {
                    ok = true,
                    proxyPort = _proxyPort,
                    lmStudioEndpoint = $"http://{_lmStudioHost}:{_lmStudioPort}/v1/chat/completions",
                    chatModel = ChatModel
                }));

            server.Map("POST", "/api/chat", (connection, request, cancellationToken) =>
                HandleChatUiRequest(request, cancellationToken));

            server.MapStream("POST", "/api/chat-stream", async (connection, request, stream, cancellationToken) =>
                await HandleChatUiStreamRequest(request, stream, cancellationToken));

            LogMessage($"[Chat UI] Created HttpServer at {ChatServerUrl} for LM Studio http://{_lmStudioHost}:{_lmStudioPort}/v1/chat/completions");
            return server;
        }

        private string HandleChatUiRequest(HttpRequest request, CancellationToken cancellationToken) {
            try {
                string lmRequestJson = BuildChatUiCompletionRequestJson(request?.Body, streamResponses: false);
                string url = $"http://{_lmStudioHost}:{_lmStudioPort}/v1/chat/completions";

                using (var content = new StringContent(lmRequestJson, Encoding.UTF8, "application/json"))
                using (var response = _httpClient.PostAsync(url, content, cancellationToken).GetAwaiter().GetResult()) {
                    string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode) {
                        LogMessage($"[Chat UI] LM Studio returned {(int)response.StatusCode} {response.ReasonPhrase}: {TruncateForLog(responseBody, 1000)}");
                        return JsonSerializer.Serialize(new {
                            ok = false,
                            status = (int)response.StatusCode,
                            error = response.ReasonPhrase ?? "LM Studio request failed.",
                            body = responseBody
                        });
                    }

                    var completion = ExtractChatUiCompletionFromChatCompletion(responseBody);
                    return JsonSerializer.Serialize(new {
                        ok = true,
                        content = completion.Content,
                        reasoning = completion.Reasoning,
                        raw = responseBody
                    });
                }
            } catch (OperationCanceledException) {
                return JsonSerializer.Serialize(new {
                    ok = false,
                    error = "The chat request was cancelled."
                });
            } catch (Exception ex) {
                LogMessage($"[Chat UI] Request failed: {ex}");
                return JsonSerializer.Serialize(new {
                    ok = false,
                    error = ex.Message
                });
            }
        }

        private async Task HandleChatUiStreamRequest(HttpRequest request, ChunkedStream output, CancellationToken cancellationToken) {
            output.ContentType = "application/x-ndjson";

            try {
                string lmRequestJson = BuildChatUiCompletionRequestJson(request?.Body, streamResponses: true);
                string url = $"http://{_lmStudioHost}:{_lmStudioPort}/v1/chat/completions";

                using (var content = new StringContent(lmRequestJson, Encoding.UTF8, "application/json"))
                using (var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, url)) {
                    upstreamRequest.Content = content;

                    using (var response = await _httpClient.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)) {
                        if (!response.IsSuccessStatusCode) {
                            string body = response.Content == null ? "" : await response.Content.ReadAsStringAsync();
                            LogMessage($"[Chat UI] Streaming LM Studio returned {(int)response.StatusCode} {response.ReasonPhrase}: {TruncateForLog(body, 1000)}");
                            WriteChatUiStreamEvent(output, "error", response.ReasonPhrase ?? "LM Studio streaming request failed.", "", body);
                            return;
                        }

                        using (var upstreamStream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(upstreamStream, Encoding.UTF8)) {
                            string line;
                            while (!cancellationToken.IsCancellationRequested &&
                                   (line = await reader.ReadLineAsync()) != null) {
                                if (string.IsNullOrWhiteSpace(line))
                                    continue;

                                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string payload = line.Substring(5).Trim();
                                if (payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                                    break;

                                foreach (var delta in ExtractChatUiStreamingDeltas(payload)) {
                                    if (!string.IsNullOrEmpty(delta.Content) || !string.IsNullOrEmpty(delta.Reasoning))
                                        WriteChatUiStreamEvent(output, "delta", delta.Content, delta.Reasoning, "");
                                }
                            }
                        }
                    }
                }

                WriteChatUiStreamEvent(output, "done", "", "", "");
            } catch (OperationCanceledException) {
                WriteChatUiStreamEvent(output, "done", "", "", "");
            } catch (Exception ex) {
                LogMessage($"[Chat UI] Streaming request failed: {ex}");
                WriteChatUiStreamEvent(output, "error", ex.Message, "", "");
            }
        }

        private void WriteChatUiStreamEvent(ChunkedStream output, string type, string content, string reasoning, string body) {
            output.WriteLine(JsonSerializer.Serialize(new {
                type = type ?? "delta",
                content = content ?? "",
                reasoning = reasoning ?? "",
                body = body ?? ""
            }));
        }

        private IEnumerable<ChatUiCompletion> ExtractChatUiStreamingDeltas(string payload) {
            if (string.IsNullOrWhiteSpace(payload))
                yield break;

            JsonDocument document = null;
            try {
                document = JsonDocument.Parse(payload);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("choices", out JsonElement choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                    yield break;

                foreach (JsonElement choice in choices.EnumerateArray()) {
                    JsonElement delta;
                    if (choice.TryGetProperty("delta", out delta) && delta.ValueKind == JsonValueKind.Object) {
                        yield return ExtractChatUiDelta(delta);
                    } else if (choice.TryGetProperty("message", out delta) && delta.ValueKind == JsonValueKind.Object) {
                        yield return ExtractChatUiDelta(delta);
                    }
                }
            } finally {
                document?.Dispose();
            }
        }

        private ChatUiCompletion ExtractChatUiDelta(JsonElement delta) {
            string content = delta.TryGetProperty("content", out JsonElement contentElement)
                ? ExtractResponsesContentText(contentElement)
                : "";
            string reasoning = ExtractReasoningText(delta);
            return new ChatUiCompletion {
                Content = content ?? "",
                Reasoning = reasoning ?? ""
            };
        }

        private string BuildChatUiCompletionRequestJson(string requestBody, bool streamResponses) {
            if (string.IsNullOrWhiteSpace(requestBody))
                throw new InvalidOperationException("Chat UI request body was empty.");

            using (JsonDocument document = JsonDocument.Parse(requestBody))
            using (var jsonStream = new MemoryStream())
            using (var writer = new Utf8JsonWriter(jsonStream)) {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Chat UI request body must be a JSON object.");

                writer.WriteStartObject();
                writer.WriteString("model", string.IsNullOrWhiteSpace(ChatModel) ? "lm-studio" : ChatModel);
                writer.WriteBoolean("stream", streamResponses);

                if (root.TryGetProperty("temperature", out JsonElement temperature))
                    WritePropertyIfSimple(writer, "temperature", temperature);

                writer.WritePropertyName("messages");
                writer.WriteStartArray();

                int messageCount = 0;
                if (root.TryGetProperty("messages", out JsonElement messages) &&
                    messages.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement message in messages.EnumerateArray())
                        WriteChatUiMessage(writer, message, ref messageCount);
                }

                if (messageCount == 0) {
                    string message = ExtractStringProperty(root, "message") ??
                                     ExtractStringProperty(root, "prompt") ??
                                     "";
                    if (!string.IsNullOrWhiteSpace(message)) {
                        writer.WriteStartObject();
                        writer.WriteString("role", "user");
                        writer.WriteString("content", message);
                        writer.WriteEndObject();
                        messageCount++;
                    }
                }

                if (messageCount == 0)
                    throw new InvalidOperationException("Chat UI request did not include a message.");

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                return Encoding.UTF8.GetString(jsonStream.ToArray());
            }
        }

        private void WriteChatUiMessage(Utf8JsonWriter writer, JsonElement message, ref int messageCount) {
            if (message.ValueKind != JsonValueKind.Object)
                return;

            string role = ExtractStringProperty(message, "role");
            if (string.IsNullOrWhiteSpace(role))
                role = "user";
            role = role.Trim().ToLowerInvariant();

            if (!IsAllowedChatUiRole(role))
                return;

            writer.WriteStartObject();
            writer.WriteString("role", role);

            bool wroteContent = false;
            if (message.TryGetProperty("content", out JsonElement contentElement))
                wroteContent = WriteChatUiContent(writer, contentElement);

            if (!wroteContent) {
                string content = ExtractStringProperty(message, "text") ?? "";
                if (string.IsNullOrWhiteSpace(content)) {
                    writer.WriteEndObject();
                    return;
                }

                writer.WriteString("content", content);
            }

            writer.WriteEndObject();
            messageCount++;
        }

        private bool WriteChatUiContent(Utf8JsonWriter writer, JsonElement contentElement) {
            if (contentElement.ValueKind == JsonValueKind.String) {
                string content = contentElement.GetString();
                if (string.IsNullOrWhiteSpace(content))
                    return false;

                writer.WriteString("content", content);
                return true;
            }

            if (contentElement.ValueKind != JsonValueKind.Array)
                return false;

            var validParts = new List<JsonElement>();
            foreach (JsonElement part in contentElement.EnumerateArray()) {
                if (IsSupportedChatUiContentPart(part))
                    validParts.Add(part);
            }

            if (validParts.Count == 0)
                return false;

            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (JsonElement part in validParts)
                WriteChatUiContentPart(writer, part);
            writer.WriteEndArray();
            return true;
        }

        private bool IsSupportedChatUiContentPart(JsonElement part) {
            if (part.ValueKind != JsonValueKind.Object)
                return false;

            string type = ExtractStringProperty(part, "type") ?? "";
            if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(ExtractStringProperty(part, "text") ?? ExtractStringProperty(part, "content"));

            if (type.Equals("image_url", StringComparison.OrdinalIgnoreCase))
                return TryExtractChatUiImageUrl(part, out _);

            return false;
        }

        private void WriteChatUiContentPart(Utf8JsonWriter writer, JsonElement part) {
            string type = ExtractStringProperty(part, "type") ?? "";
            writer.WriteStartObject();

            if (type.Equals("text", StringComparison.OrdinalIgnoreCase)) {
                writer.WriteString("type", "text");
                writer.WriteString("text", ExtractStringProperty(part, "text") ?? ExtractStringProperty(part, "content") ?? "");
            } else if (type.Equals("image_url", StringComparison.OrdinalIgnoreCase) &&
                       TryExtractChatUiImageUrl(part, out string imageUrl)) {
                writer.WriteString("type", "image_url");
                writer.WritePropertyName("image_url");
                writer.WriteStartObject();
                writer.WriteString("url", imageUrl);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        private bool TryExtractChatUiImageUrl(JsonElement part, out string imageUrl) {
            imageUrl = null;
            if (part.ValueKind != JsonValueKind.Object)
                return false;

            if (part.TryGetProperty("image_url", out JsonElement imageUrlElement)) {
                if (imageUrlElement.ValueKind == JsonValueKind.String) {
                    imageUrl = imageUrlElement.GetString();
                } else if (imageUrlElement.ValueKind == JsonValueKind.Object) {
                    imageUrl = ExtractStringProperty(imageUrlElement, "url");
                }
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
                imageUrl = ExtractStringProperty(part, "url");

            return !string.IsNullOrWhiteSpace(imageUrl) &&
                   (imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
                    imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsAllowedChatUiRole(string role) {
            return role.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                   role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                   role.Equals("assistant", StringComparison.OrdinalIgnoreCase);
        }

        private ChatUiCompletion ExtractChatUiCompletionFromChatCompletion(string responseBody) {
            if (string.IsNullOrWhiteSpace(responseBody))
                return new ChatUiCompletion();

            try {
                using (JsonDocument document = JsonDocument.Parse(responseBody)) {
                    JsonElement root = document.RootElement;
                    if (!root.TryGetProperty("choices", out JsonElement choices) ||
                        choices.ValueKind != JsonValueKind.Array ||
                        choices.GetArrayLength() == 0)
                        return SplitThinkTags(responseBody, "");

                    JsonElement choice = choices[0];
                    if (choice.TryGetProperty("message", out JsonElement message) &&
                        message.ValueKind == JsonValueKind.Object) {
                        string content = message.TryGetProperty("content", out JsonElement messageContent)
                            ? ExtractResponsesContentText(messageContent)
                            : "";
                        string reasoning = ExtractReasoningText(message);
                        return SplitThinkTags(content, reasoning);
                    }

                    if (choice.TryGetProperty("delta", out JsonElement delta) &&
                        delta.ValueKind == JsonValueKind.Object) {
                        string content = delta.TryGetProperty("content", out JsonElement deltaContent)
                            ? ExtractResponsesContentText(deltaContent)
                            : "";
                        string reasoning = ExtractReasoningText(delta);
                        return SplitThinkTags(content, reasoning);
                    }
                }
            } catch {
                return SplitThinkTags(responseBody, "");
            }

            return new ChatUiCompletion();
        }

        private string ExtractReasoningText(JsonElement element) {
            string reasoning =
                ExtractStringProperty(element, "reasoning_content") ??
                ExtractStringProperty(element, "reasoning") ??
                ExtractStringProperty(element, "thinking") ??
                ExtractStringProperty(element, "thought") ??
                ExtractStringProperty(element, "analysis");

            if (!string.IsNullOrWhiteSpace(reasoning))
                return reasoning;

            if (element.TryGetProperty("reasoning", out JsonElement reasoningElement))
                return ExtractResponsesContentText(reasoningElement);

            return "";
        }

        private ChatUiCompletion SplitThinkTags(string content, string reasoning) {
            content = content ?? "";
            reasoning = reasoning ?? "";

            const string openTag = "<think>";
            const string closeTag = "</think>";
            int openIndex = content.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            int closeIndex = content.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase);

            if (openIndex >= 0 && closeIndex > openIndex) {
                int thoughtStart = openIndex + openTag.Length;
                string taggedReasoning = content.Substring(thoughtStart, closeIndex - thoughtStart).Trim();
                if (!string.IsNullOrWhiteSpace(taggedReasoning)) {
                    reasoning = string.IsNullOrWhiteSpace(reasoning)
                        ? taggedReasoning
                        : reasoning.Trim() + Environment.NewLine + Environment.NewLine + taggedReasoning;
                }

                content = (content.Substring(0, openIndex) + content.Substring(closeIndex + closeTag.Length)).Trim();
            }

            return new ChatUiCompletion {
                Content = content,
                Reasoning = reasoning?.Trim() ?? ""
            };
        }

        private string BuildChatUiHtml() {
            string html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>LmVsProxy Chat</title>
<style>
:root {
    color-scheme: dark;
    --bg: #0c1117;
    --panel: rgba(17, 24, 33, 0.84);
    --panel-strong: rgba(20, 30, 42, 0.96);
    --line: rgba(148, 163, 184, 0.22);
    --text: #e5edf7;
    --muted: #94a3b8;
    --accent: #40c9a2;
    --accent-2: #7dd3fc;
    --danger: #fb7185;
}
* { box-sizing: border-box; }
body {
    margin: 0;
    min-height: 100vh;
    font-family: ""Segoe UI"", ""Cascadia Code"", sans-serif;
    background:
        radial-gradient(circle at top left, rgba(64, 201, 162, 0.22), transparent 34rem),
        radial-gradient(circle at bottom right, rgba(125, 211, 252, 0.16), transparent 30rem),
        var(--bg);
    color: var(--text);
}
.shell {
    width: min(1040px, calc(100vw - 28px));
    min-height: calc(100vh - 28px);
    margin: 14px auto;
    display: grid;
    grid-template-rows: auto 1fr auto;
    gap: 12px;
}
.topbar, .composer, .messages {
    border: 1px solid var(--line);
    background: var(--panel);
    box-shadow: 0 18px 70px rgba(0, 0, 0, 0.28);
    backdrop-filter: blur(18px);
}
.topbar {
    border-radius: 22px;
    padding: 18px 20px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
}
h1 {
    margin: 0;
    font-size: clamp(1.25rem, 3vw, 2rem);
    letter-spacing: -0.04em;
}
.meta {
    color: var(--muted);
    font-family: ""Cascadia Code"", monospace;
    font-size: 0.82rem;
}
.pill {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    border: 1px solid rgba(64, 201, 162, 0.4);
    color: #c7fff0;
    background: rgba(64, 201, 162, 0.1);
    border-radius: 999px;
    padding: 8px 12px;
    white-space: nowrap;
}
.dot {
    width: 8px;
    height: 8px;
    border-radius: 99px;
    background: var(--accent);
    box-shadow: 0 0 18px var(--accent);
}
.messages {
    border-radius: 26px;
    padding: 18px;
    overflow: auto;
}
.empty {
    height: 100%;
    min-height: 42vh;
    display: grid;
    place-items: center;
    color: var(--muted);
    text-align: center;
}
.message {
    max-width: 82%;
    margin: 0 0 14px;
    padding: 14px 16px;
    border-radius: 18px;
    line-height: 1.45;
    white-space: pre-wrap;
}
.message.user {
    margin-left: auto;
    background: linear-gradient(135deg, rgba(64, 201, 162, 0.28), rgba(125, 211, 252, 0.16));
    border: 1px solid rgba(64, 201, 162, 0.38);
}
.image-grid {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-top: 10px;
}
.image-grid img {
    width: 118px;
    height: 88px;
    object-fit: cover;
    border-radius: 12px;
    border: 1px solid rgba(255, 255, 255, 0.18);
}
.message.assistant {
    background: var(--panel-strong);
    border: 1px solid var(--line);
}
.thinking {
    margin: 0 0 12px;
    border: 1px solid rgba(125, 211, 252, 0.28);
    border-radius: 14px;
    background: rgba(125, 211, 252, 0.07);
    overflow: hidden;
}
.thinking[hidden] {
    display: none;
}
.thinking summary {
    cursor: pointer;
    color: #bfdbfe;
    font-weight: 800;
    padding: 10px 12px;
    user-select: none;
}
.thinking pre {
    margin: 0;
    border-top: 1px solid rgba(125, 211, 252, 0.18);
    padding: 12px;
    color: #c4d6f0;
    font: 0.9rem/1.45 ""Cascadia Code"", monospace;
    white-space: pre-wrap;
}
.answer {
    white-space: pre-wrap;
}
.message.error {
    background: rgba(251, 113, 133, 0.12);
    border: 1px solid rgba(251, 113, 133, 0.4);
    color: #fecdd3;
}
.composer {
    border-radius: 24px;
    padding: 12px;
    display: grid;
    grid-template-columns: auto 1fr auto;
    gap: 10px;
    align-items: stretch;
}
textarea {
    width: 100%;
    min-height: 74px;
    max-height: 30vh;
    resize: vertical;
    border: 0;
    outline: 0;
    border-radius: 16px;
    padding: 14px 16px;
    color: var(--text);
    background: rgba(2, 6, 23, 0.54);
    font: 0.98rem/1.45 ""Cascadia Code"", monospace;
}
.attach {
    display: grid;
    place-items: center;
    min-width: 54px;
    border-radius: 16px;
    border: 1px solid rgba(125, 211, 252, 0.24);
    color: #d8f3ff;
    background: rgba(125, 211, 252, 0.1);
    font-weight: 900;
    cursor: pointer;
}
.attach input {
    display: none;
}
.preview-tray {
    grid-column: 1 / -1;
    display: none;
    gap: 8px;
    flex-wrap: wrap;
    padding: 4px;
}
.preview-tray.has-images {
    display: flex;
}
.preview {
    position: relative;
}
.preview img {
    width: 86px;
    height: 64px;
    object-fit: cover;
    border-radius: 12px;
    border: 1px solid rgba(255, 255, 255, 0.18);
}
.preview button {
    position: absolute;
    top: -7px;
    right: -7px;
    width: 22px;
    height: 22px;
    min-height: 0;
    padding: 0;
    border-radius: 99px;
    color: #fff;
    background: var(--danger);
    font-size: 0.78rem;
}
button {
    border: 0;
    border-radius: 16px;
    padding: 0 20px;
    color: #031310;
    background: linear-gradient(135deg, var(--accent), var(--accent-2));
    font-weight: 800;
    cursor: pointer;
}
button:disabled {
    cursor: wait;
    opacity: 0.62;
}
@media (max-width: 700px) {
    .topbar { align-items: flex-start; flex-direction: column; }
    .composer { grid-template-columns: auto 1fr; }
    .composer > button { grid-column: 1 / -1; }
    button { min-height: 46px; }
    .message { max-width: 100%; }
}
</style>
</head>
<body>
<main class=""shell"">
    <header class=""topbar"">
        <div>
            <h1>LmVsProxy Chat</h1>
            <div class=""meta"">model: $CHAT_MODEL | upstream: $LM_ENDPOINT</div>
        </div>
        <div class=""pill""><span class=""dot""></span>local chat server</div>
    </header>
    <section id=""messages"" class=""messages"">
        <div class=""empty"">Ask the loaded LM Studio model anything. This UI talks through LmVsProxy's SocketJack HttpServer.</div>
    </section>
    <form id=""composer"" class=""composer"">
        <label class=""attach"" title=""Attach images"">
            +
            <input id=""imageInput"" type=""file"" accept=""image/*"" multiple>
        </label>
        <textarea id=""prompt"" placeholder=""Type a message. Shift+Enter makes a new line.""></textarea>
        <button id=""send"" type=""submit"">Send</button>
        <div id=""previewTray"" class=""preview-tray""></div>
    </form>
</main>
<script>
const messages = [];
const list = document.getElementById('messages');
const form = document.getElementById('composer');
const prompt = document.getElementById('prompt');
const send = document.getElementById('send');
const imageInput = document.getElementById('imageInput');
const previewTray = document.getElementById('previewTray');
let pendingImages = [];

function clearEmpty() {
    const empty = list.querySelector('.empty');
    if (empty) empty.remove();
}

function addMessage(role, content) {
    clearEmpty();
    const item = document.createElement('div');
    item.className = 'message ' + role;
    item.textContent = content || '';
    list.appendChild(item);
    list.scrollTop = list.scrollHeight;
    return item;
}

function addUserMessage(content, images) {
    clearEmpty();
    const item = document.createElement('div');
    item.className = 'message user';
    if (content) {
        const text = document.createElement('div');
        text.textContent = content;
        item.appendChild(text);
    }
    if (images && images.length) {
        const grid = document.createElement('div');
        grid.className = 'image-grid';
        for (const image of images) {
            const img = document.createElement('img');
            img.src = image.url;
            img.alt = image.name || 'attached image';
            grid.appendChild(img);
        }
        item.appendChild(grid);
    }
    list.appendChild(item);
    list.scrollTop = list.scrollHeight;
    return item;
}

function setAssistantMessage(item, content, reasoning) {
    item.textContent = '';
    const parts = createAssistantParts(item);
    updateAssistantParts(parts, content, reasoning);
}

function createAssistantParts(item) {
    item.textContent = '';
    const details = document.createElement('details');
    details.className = 'thinking';
    details.open = true;
    details.hidden = true;
    const summary = document.createElement('summary');
    summary.textContent = 'Thinking';
    const pre = document.createElement('pre');
    details.appendChild(summary);
    details.appendChild(pre);
    item.appendChild(details);

    const answer = document.createElement('div');
    answer.className = 'answer';
    item.appendChild(answer);
    return { details, pre, answer };
}

function updateAssistantParts(parts, content, reasoning) {
    const cleanReasoning = (reasoning || '').trim();
    parts.details.hidden = !cleanReasoning;
    parts.pre.textContent = cleanReasoning;
    parts.answer.textContent = content || '';
    list.scrollTop = list.scrollHeight;
}

function splitThinkTags(text) {
    let answer = '';
    let reasoning = '';
    let cursor = 0;
    const lower = text.toLowerCase();

    while (cursor < text.length) {
        const open = lower.indexOf('<think>', cursor);
        if (open < 0) {
            answer += text.slice(cursor);
            break;
        }

        answer += text.slice(cursor, open);
        const thoughtStart = open + 7;
        const close = lower.indexOf('</think>', thoughtStart);
        if (close < 0) {
            reasoning += text.slice(thoughtStart);
            break;
        }

        reasoning += text.slice(thoughtStart, close) + '\n\n';
        cursor = close + 8;
    }

    return { content: answer.trimStart(), reasoning: reasoning.trim() };
}

function renderPreviews() {
    previewTray.textContent = '';
    previewTray.classList.toggle('has-images', pendingImages.length > 0);
    pendingImages.forEach((image, index) => {
        const preview = document.createElement('div');
        preview.className = 'preview';
        const img = document.createElement('img');
        img.src = image.url;
        img.alt = image.name || 'attached image';
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.textContent = 'x';
        remove.title = 'Remove image';
        remove.addEventListener('click', () => {
            pendingImages.splice(index, 1);
            renderPreviews();
        });
        preview.appendChild(img);
        preview.appendChild(remove);
        previewTray.appendChild(preview);
    });
}

async function addImageFiles(files) {
    const imageFiles = Array.from(files || []).filter(file => file.type && file.type.startsWith('image/'));
    for (const file of imageFiles) {
        const url = await readFileAsDataUrl(file);
        pendingImages.push({ name: file.name, url });
    }
    renderPreviews();
}

function readFileAsDataUrl(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => reject(reader.error || new Error('Could not read image file.'));
        reader.readAsDataURL(file);
    });
}

function makeUserContent(text, images) {
    const parts = [];
    if (text) parts.push({ type: 'text', text });
    for (const image of images) {
        parts.push({ type: 'image_url', image_url: { url: image.url } });
    }
    return parts.length === 1 && parts[0].type === 'text' ? text : parts;
}

async function sendMessage() {
    const text = prompt.value.trim();
    const images = pendingImages.slice();
    if ((!text && images.length === 0) || send.disabled) return;
    prompt.value = '';
    pendingImages = [];
    renderPreviews();

    const userContent = makeUserContent(text, images);
    messages.push({ role: 'user', content: userContent });
    addUserMessage(text, images);
    const pending = addMessage('assistant', 'Thinking...');
    const parts = createAssistantParts(pending);
    send.disabled = true;
    let rawContent = '';
    let explicitReasoning = '';

    try {
        const response = await fetch('/api/chat-stream', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ messages: messages.slice(-16) })
        });
        if (!response.ok) throw new Error('Chat stream failed: HTTP ' + response.status);
        if (!response.body) throw new Error('This browser did not expose a readable response stream.');

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        let done = false;

        while (!done) {
            const read = await reader.read();
            done = read.done;
            buffer += decoder.decode(read.value || new Uint8Array(), { stream: !done });
            const lines = buffer.split('\n');
            buffer = lines.pop() || '';

            for (const line of lines) {
                const trimmed = line.trim();
                if (!trimmed) continue;
                const event = JSON.parse(trimmed);
                if (event.type === 'error') throw new Error(event.content || event.body || 'LM Studio streaming request failed.');
                if (event.type === 'done') {
                    done = true;
                    break;
                }

                rawContent += event.content || '';
                explicitReasoning += event.reasoning || '';
                const split = splitThinkTags(rawContent);
                const reasoning = [explicitReasoning.trim(), split.reasoning.trim()].filter(Boolean).join('\n\n');
                updateAssistantParts(parts, split.content, reasoning);
            }
        }

        const finalSplit = splitThinkTags(rawContent);
        const finalContent = finalSplit.content || parts.answer.textContent || '(empty response)';
        const finalReasoning = [explicitReasoning.trim(), finalSplit.reasoning.trim()].filter(Boolean).join('\n\n');
        updateAssistantParts(parts, finalContent, finalReasoning);
        messages.push({ role: 'assistant', content: finalContent });
    } catch (error) {
        pending.className = 'message error';
        pending.textContent = error && error.message ? error.message : String(error);
    } finally {
        send.disabled = false;
        prompt.focus();
    }
}

form.addEventListener('submit', event => {
    event.preventDefault();
    sendMessage();
});

prompt.addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.shiftKey) {
        event.preventDefault();
        sendMessage();
    }
});

imageInput.addEventListener('change', event => {
    addImageFiles(event.target.files).catch(error => {
        addMessage('error', error && error.message ? error.message : String(error));
    });
    imageInput.value = '';
});

window.addEventListener('paste', event => {
    const files = event.clipboardData && event.clipboardData.files;
    if (files && files.length) {
        addImageFiles(files).catch(error => {
            addMessage('error', error && error.message ? error.message : String(error));
        });
    }
});

form.addEventListener('dragover', event => {
    event.preventDefault();
});

form.addEventListener('drop', event => {
    event.preventDefault();
    addImageFiles(event.dataTransfer && event.dataTransfer.files).catch(error => {
        addMessage('error', error && error.message ? error.message : String(error));
    });
});

prompt.focus();
</script>
</body>
</html>";

            return html
                .Replace("$CHAT_MODEL", WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(ChatModel) ? "lm-studio" : ChatModel))
                .Replace("$LM_ENDPOINT", WebUtility.HtmlEncode($"http://{_lmStudioHost}:{_lmStudioPort}/v1/chat/completions"));
        }

        private void StopChatServer() {
            var server = _chatServer;
            if (server == null)
                return;

            try {
                if (server.IsListening)
                    server.StopListening();
            } catch (Exception ex) {
                LogMessage($"[Chat UI] Stop failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Main listener loop that accepts incoming HTTP requests.
        /// </summary>
        private async Task ListenerLoop(CancellationToken ct) {
            while (_isRunning && !ct.IsCancellationRequested) {
                try {
                    HttpListenerContext context = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => ProcessRequest(context), ct);
                } catch (ObjectDisposedException) {
                    break;
                } catch (Exception ex) {
                    LogMessage($"Listener error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Processes a single HTTP request.
        /// </summary>
        private async Task ProcessRequest(HttpListenerContext context) {
            try {
                HttpListenerRequest req = context.Request;
                HttpListenerResponse resp = context.Response;

                // Act as a transparent proxy for all /v1/* endpoints and methods.
                bool isV1Proxy = req.Url.AbsolutePath != null && req.Url.AbsolutePath.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
                if (!isV1Proxy) {
                    resp.StatusCode = 404;
                    resp.Close();
                    return;
                }

                // Read request body
                string requestBody = null;
                using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding)) {
                    requestBody = await reader.ReadToEndAsync();
                }

                requestBody = EscapeInvalidJsonStringControlCharacters(requestBody, out int escapedControlCharacters);
                if (escapedControlCharacters > 0)
                    LogMessage($"[JSON Repair] Escaped {escapedControlCharacters} raw control character(s) inside JSON strings before forwarding.");

                requestBody = ScrubDeadEndProjectContextMessage(requestBody);
                requestBody = PrepareRequestBodyForLmStudio(req, requestBody);
                LogRequestDiagnostics(req, requestBody);

                if (IsResponsesEndpoint(req)) {
                    await ForwardResponsesAsChatCompletionsAsync(req, requestBody, resp);
                    return;
                }

                if (IsStreamingChatCompletion(req, requestBody)) {
                    await ForwardStreamingChatCompletionWithKeepAliveAsync(req, requestBody, resp);
                    return;
                }

                // Transparent proxy to LM Studio for all /v1/* endpoints.
                try {
                    var lmResponseMsg = await ForwardRawRequestToLmStudioAsync(req, requestBody);
                    if (lmResponseMsg == null) {
                        SendErrorResponse(resp, "Failed to get response from LM Studio.");
                        return;
                    }

                    LogMessage($"[HTTP] LM Studio response {(int)lmResponseMsg.StatusCode} {lmResponseMsg.ReasonPhrase} for {req.HttpMethod} {req.Url.AbsolutePath}");

                    // Copy status code
                    resp.StatusCode = (int)lmResponseMsg.StatusCode;

                    // Copy response headers
                    CopyResponseHeaders(lmResponseMsg, resp);

                    // Stream response body without buffering to preserve SSE/tool-call framing.
                    using (var upstreamStream = await lmResponseMsg.Content.ReadAsStreamAsync())
                    {
                        await upstreamStream.CopyToAsync(resp.OutputStream);
                    }
                    resp.Close();
                    return;
                } catch (Exception ex) {
                    LogMessage($"[HTTP] Proxy error for {req.HttpMethod} {req.Url.AbsolutePath}: {ex}");
                    SendErrorResponse(resp, "Proxy error: " + ex.Message);
                    return;
                }
            } catch (Exception ex) {
                LogMessage($"[HTTP] Error processing request: {ex}");
                try {
                    SendErrorResponse(context.Response, $"Error processing request: {ex.Message}");
                } catch { }
            }
        }

        /// <summary>
        /// Sends an error response.
        /// </summary>
        private void SendErrorResponse(HttpListenerResponse resp, string errorMessage) {
            LogMessage($"[HTTP] Sending error response: {errorMessage}");
            string response = "{ \"error\": \"" + EscapeJson(errorMessage) + "\", \"tool_calls\": null }";
            byte[] buffer = Encoding.UTF8.GetBytes(response);
            resp.ContentType = "application/json";
            resp.ContentLength64 = buffer.Length;
            resp.StatusCode = 400;
            resp.OutputStream.Write(buffer, 0, buffer.Length);
            resp.Close();
        }

        /// <summary>
        /// Parses a JSON request string into a VsRequest object using regex.
        /// </summary>
        private VsRequest ParseVsRequest(string json) {
            var request = new VsRequest {
                messages = new List<Dictionary<string, object>>(),
                tools = new List<Dictionary<string, object>>()
            };

            try {
                // Extract model
                var modelMatch = System.Text.RegularExpressions.Regex.Match(json, @"""model""\s*:\s*""([^""]*)""");
                if (modelMatch.Success)
                    request.model = modelMatch.Groups[1].Value;

                // Extract temperature
                var tempMatch = System.Text.RegularExpressions.Regex.Match(json, @"""temperature""\s*:\s*([0-9.]+)");
                if (tempMatch.Success && double.TryParse(tempMatch.Groups[1].Value, out var temp))
                    request.temperature = temp;

                // Extract max_tokens
                var maxTokensMatch = System.Text.RegularExpressions.Regex.Match(json, @"""max_tokens""\s*:\s*(\d+)");
                if (maxTokensMatch.Success && int.TryParse(maxTokensMatch.Groups[1].Value, out var maxTokens))
                    request.max_tokens = maxTokens;

                // Note: Full message and tool parsing would require a JSON library
                // For this version, we'll store the raw JSON sections
                request.rawJson = json;

                return request;
            } catch {
                return request;
            }
        }

        /// <summary>
        /// Converts Visual Studio tool schema to OpenAI function schema (as JSON strings).
        /// </summary>
        private string[] TranslateToolsToOpenAi(List<Dictionary<string, object>> vsTools) {
            var openAiTools = new List<string>();

            foreach (var vsTool in vsTools) {
                // Convert GitHub tool format to OpenAI format
                // For now, we'll just pass through the tool as-is
                // In production, you'd strip GitHub-specific metadata here
                try {
                    string toolJson = SerializeToJson(vsTool);
                    openAiTools.Add(toolJson);
                } catch {
                    // Skip invalid tools
                }
            }

            return openAiTools.Count > 0 ? openAiTools.ToArray() : null;
        }

        /// <summary>
        /// Simple JSON serialization helper.
        /// </summary>
        private string SerializeToJson(object obj) {
            if (obj == null) return "null";
            if (obj is string str) return "\"" + EscapeJson(str) + "\"";
            if (obj is bool b) return b ? "true" : "false";
            if (obj is int || obj is long || obj is double || obj is float) return obj.ToString();
            if (obj is Dictionary<string, object> dict) {
                var sb = new StringBuilder("{");
                bool first = true;
                foreach (var kv in dict) {
                    if (!first) sb.Append(",");
                    sb.Append("\"").Append(kv.Key).Append("\":").Append(SerializeToJson(kv.Value));
                    first = false;
                }
                sb.Append("}");
                return sb.ToString();
            }
            if (obj is List<object> list) {
                var sb = new StringBuilder("[");
                bool first = true;
                foreach (var item in list) {
                    if (!first) sb.Append(",");
                    sb.Append(SerializeToJson(item));
                    first = false;
                }
                sb.Append("]");
                return sb.ToString();
            }
            return "\"" + EscapeJson(obj.ToString()) + "\"";
        }

        /// <summary>
        /// Builds a request JSON for LM Studio.
        /// </summary>
        private string BuildLmStudioRequest(VsRequest vsRequest, string[] functions) {
            var sb = new StringBuilder();
            sb.Append("{ \"model\": \"").Append(EscapeJson(vsRequest.model)).Append("\", ");
            sb.Append("\"messages\": ");

            // Extract messages from raw JSON
            var messagesMatch = System.Text.RegularExpressions.Regex.Match(vsRequest.rawJson, @"""messages""\s*:\s*(\[[^\]]*\])");
            if (messagesMatch.Success) {
                sb.Append(messagesMatch.Groups[1].Value);
            } else {
                sb.Append("[]");
            }

            sb.Append(", ");

            // Add functions if present
            if (functions != null && functions.Length > 0) {
                sb.Append("\"functions\": [");
                for (int i = 0; i < functions.Length; i++) {
                    if (i > 0) sb.Append(", ");
                    sb.Append(functions[i]);
                }
                sb.Append("], ");
            }

            // Add optional parameters
            if (vsRequest.temperature.HasValue)
                sb.Append("\"temperature\": ").Append(vsRequest.temperature.Value).Append(", ");

            if (vsRequest.max_tokens.HasValue)
                sb.Append("\"max_tokens\": ").Append(vsRequest.max_tokens.Value).Append(", ");

            sb.Append("\"temperature\": 0.7 }");
            return sb.ToString();
        }

        /// <summary>
        /// Sends a request to LM Studio's /v1/chat/completions endpoint.
        /// </summary>
        private async Task<string> SendToLmStudioAsync(string requestJson) {
            try {
                string url = $"http://{_lmStudioHost}:{_lmStudioPort}/v1/chat/completions";

                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);

                if ((int)response.StatusCode >= 400)
                    return null;

                string responseBody = await response.Content.ReadAsStringAsync();
                return responseBody;
            } catch (Exception ex) {
                LogMessage($"Error sending to LM Studio: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Forwards a raw /v1/* request to LM Studio, preserving body and relevant headers.
        /// </summary>
        private async Task<HttpResponseMessage> ForwardRawRequestToLmStudioAsync(HttpListenerRequest originalRequest, string requestBody) {
            if (originalRequest == null)
                throw new ArgumentNullException(nameof(originalRequest));

            string pathAndQuery = originalRequest.Url?.PathAndQuery ?? "/v1/chat/completions";
            string url = $"http://{_lmStudioHost}:{_lmStudioPort}{pathAndQuery}";
            LogMessage($"[HTTP] Forwarding {originalRequest.HttpMethod} {pathAndQuery} to LM Studio; bodyLength={(requestBody ?? "").Length}");

            var forwardRequest = new HttpRequestMessage(new HttpMethod(originalRequest.HttpMethod), url);

            // Forward body for methods that carry one.
            bool hasBody = !string.IsNullOrEmpty(requestBody) &&
                           !string.Equals(originalRequest.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(originalRequest.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
            if (hasBody) {
                string mediaType = string.IsNullOrWhiteSpace(originalRequest.ContentType)
                    ? "application/json"
                    : originalRequest.ContentType;

                forwardRequest.Content = new StringContent(requestBody, originalRequest.ContentEncoding ?? Encoding.UTF8, mediaType);
            }

            // Forward most incoming headers except Host/Content-Length (managed by HttpClient).
            foreach (string headerName in originalRequest.Headers.AllKeys) {
                if (string.IsNullOrEmpty(headerName))
                    continue;
                if (headerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;

                string headerValue = originalRequest.Headers[headerName];
                if (string.IsNullOrEmpty(headerValue))
                    continue;

                if (!forwardRequest.Headers.TryAddWithoutValidation(headerName, headerValue) && forwardRequest.Content != null) {
                    forwardRequest.Content.Headers.TryAddWithoutValidation(headerName, headerValue);
                }
            }

            return await _httpClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead);
        }

        /// <summary>
        /// Emits concise request diagnostics without dumping the full prompt.
        /// </summary>
        private void LogRequestDiagnostics(HttpListenerRequest req, string requestBody) {
            try {
                if (req == null)
                    return;

                string path = req.Url?.AbsolutePath ?? "";
                LogMessage($"[Request] {req.HttpMethod} {path}; bodyLength={(requestBody ?? "").Length}; streaming={IsStreamingChatCompletion(req, requestBody)}");

                if (!path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(requestBody))
                    return;

                HashSet<string> tools = GetAvailableToolNames(requestBody);
                if (tools.Count == 0) {
                    LogMessage("[Request] Advertised VS tools: none");
                } else {
                    LogMessage("[Request] Advertised VS tools (" + tools.Count.ToString() + "): " +
                               TruncateForLog(string.Join(", ", tools), 900));
                }

                string toolChoice = ExtractToolChoiceForLog(requestBody);
                if (!string.IsNullOrWhiteSpace(toolChoice))
                    LogMessage("[Request] tool_choice=" + toolChoice);

                LogConversationToolDiagnostics(requestBody);
            } catch (Exception ex) {
                LogMessage($"[Request] Diagnostics error: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs tool calls and tool-result errors that Visual Studio sends back in conversation history.
        /// </summary>
        private void LogConversationToolDiagnostics(string requestBody) {
            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return;

                    var toolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonElement message in messages.EnumerateArray()) {
                        string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                      roleElement.ValueKind == JsonValueKind.String
                            ? roleElement.GetString()
                            : "";

                        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                            message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array) {
                            foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                                string id = toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                            idElement.ValueKind == JsonValueKind.String
                                    ? idElement.GetString()
                                    : "";

                                string name = "";
                                string arguments = "";
                                if (toolCall.TryGetProperty("function", out JsonElement function) &&
                                    function.ValueKind == JsonValueKind.Object) {
                                    name = function.TryGetProperty("name", out JsonElement nameElement) &&
                                           nameElement.ValueKind == JsonValueKind.String
                                        ? nameElement.GetString()
                                        : "";
                                    arguments = function.TryGetProperty("arguments", out JsonElement argumentsElement)
                                        ? argumentsElement.ToString()
                                        : "";
                                }

                                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                                    toolCallNames[id] = name;

                                if (!string.IsNullOrWhiteSpace(name))
                                    LogMessage("[Conversation Tool Call] id=" + id + " name=" + name +
                                               " args=" + TruncateForLog(arguments, 300));
                            }
                        } else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)) {
                            string toolCallId = message.TryGetProperty("tool_call_id", out JsonElement toolCallIdElement) &&
                                                toolCallIdElement.ValueKind == JsonValueKind.String
                                ? toolCallIdElement.GetString()
                                : "";
                            string toolName = !string.IsNullOrWhiteSpace(toolCallId) &&
                                              toolCallNames.TryGetValue(toolCallId, out string mappedName)
                                ? mappedName
                                : "<unknown>";
                            string content = message.TryGetProperty("content", out JsonElement contentElement)
                                ? contentElement.ToString()
                                : "";

                            if (LooksLikeToolError(content)) {
                                LogMessage("[Conversation Tool Result Error] id=" + toolCallId +
                                           " name=" + toolName + " content=" +
                                           TruncateForLog(content, 700));
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                LogMessage("[Conversation Tool Diagnostics] Error: " + ex.Message);
            }
        }

        /// <summary>
        /// Detects common Visual Studio tool-result failure text.
        /// </summary>
        private bool LooksLikeToolError(string content) {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string lower = content.ToLowerInvariant();
            return lower.Contains("couldn't run") ||
                   lower.Contains("could not run") ||
                   lower.Contains("failed to execute") ||
                   lower.Contains("invalid arguments") ||
                   lower.Contains("missing parameter") ||
                   lower.Contains("exception") ||
                   lower.Contains("error");
        }

        /// <summary>
        /// Extracts the tool_choice setting for logs.
        /// </summary>
        private string ExtractToolChoiceForLog(string requestBody) {
            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("tool_choice", out JsonElement toolChoice))
                        return null;

                    if (toolChoice.ValueKind == JsonValueKind.String)
                        return toolChoice.GetString();

                    return TruncateForLog(toolChoice.GetRawText(), 240);
                }
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Returns true when a request is a streaming OpenAI chat completion.
        /// </summary>
        private bool IsStreamingChatCompletion(HttpListenerRequest originalRequest, string requestBody) {
            if (originalRequest?.Url?.AbsolutePath == null || string.IsNullOrWhiteSpace(requestBody))
                return false;

            if (!originalRequest.Url.AbsolutePath.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                return false;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    return document.RootElement.TryGetProperty("stream", out JsonElement stream) &&
                           stream.ValueKind == JsonValueKind.True;
                }
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Returns true for Visual Studio/OpenAI Responses API calls.
        /// </summary>
        private bool IsResponsesEndpoint(HttpListenerRequest req) {
            return req?.Url?.AbsolutePath != null &&
                   req.Url.AbsolutePath.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Bridges OpenAI Responses requests from VS to LM Studio chat-completions.
        /// </summary>
        private async Task ForwardResponsesAsChatCompletionsAsync(HttpListenerRequest req, string responsesRequestBody, HttpListenerResponse resp) {
            resp.StatusCode = 200;
            resp.ContentType = "text/event-stream; charset=utf-8";
            resp.SendChunked = true;
            resp.Headers["Cache-Control"] = "no-cache";
            resp.Headers["X-Accel-Buffering"] = "no";

            try {
                string chatRequestBody = ConvertResponsesRequestToChatCompletions(responsesRequestBody);
                LogMessage("[Responses Bridge] Converted /v1/responses request to /v1/chat/completions; bodyLength=" + chatRequestBody.Length.ToString());

                Task<HttpResponseMessage> upstreamTask = ForwardChatCompletionBodyToLmStudioAsync(chatRequestBody);
                await WriteSseCommentAsync(resp.OutputStream, "proxy responses bridge keepalive");

                while (!upstreamTask.IsCompleted) {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    await WriteSseCommentAsync(resp.OutputStream, "proxy responses bridge keepalive");
                }

                using (HttpResponseMessage upstream = await upstreamTask) {
                    if (upstream == null) {
                        await WriteResponsesSseErrorAsync(resp.OutputStream, "Failed to get response from LM Studio.");
                        return;
                    }

                    LogMessage($"[Responses Bridge] LM Studio response {(int)upstream.StatusCode} {upstream.ReasonPhrase}");
                    if ((int)upstream.StatusCode >= 400) {
                        string errorBody = upstream.Content == null ? "" : await upstream.Content.ReadAsStringAsync();
                        LogMessage("[Responses Bridge] Upstream error body: " + TruncateForLog(errorBody, 800));
                        await WriteResponsesSseErrorAsync(resp.OutputStream, string.IsNullOrWhiteSpace(errorBody) ? upstream.ReasonPhrase : errorBody);
                        return;
                    }

                    string upstreamBody = upstream.Content == null ? "" : await ReadUpstreamBodyWithKeepAliveAsync(upstream.Content, resp.OutputStream);
                    upstreamBody = ScrubDeadEndProjectContextMessage(upstreamBody);
                    string chatSse = AdaptNativeOpenAiToolCallStream(upstreamBody, chatRequestBody);
                    if (ReferenceEquals(chatSse, upstreamBody))
                        chatSse = AdaptQwenToolCallStream(upstreamBody, chatRequestBody);
                    if (ReferenceEquals(chatSse, upstreamBody))
                        chatSse = AdaptTextOnlyRepairContinuation(upstreamBody, chatRequestBody);

                    string responsesSse = ConvertChatSseToResponsesSse(chatSse, chatRequestBody);
                    byte[] bytes = Encoding.UTF8.GetBytes(responsesSse);
                    await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    await resp.OutputStream.FlushAsync();
                }
            } catch (Exception ex) {
                if (IsClientDisconnectedException(ex)) {
                    LogMessage("[Responses Bridge] Client disconnected before proxy finished streaming; abandoning response.");
                    return;
                }

                LogMessage("[Responses Bridge] Error: " + ex);
                try {
                    await WriteResponsesSseErrorAsync(resp.OutputStream, "Proxy responses bridge error: " + ex.Message);
                } catch { }
            } finally {
                try {
                    resp.Close();
                } catch { }
            }
        }

        /// <summary>
        /// Sends a chat-completions request body directly to LM Studio.
        /// </summary>
        private async Task<HttpResponseMessage> ForwardChatCompletionBodyToLmStudioAsync(string chatRequestBody) {
            string url = $"http://{_lmStudioHost}:{_lmStudioPort}/v1/chat/completions";
            var forwardRequest = new HttpRequestMessage(HttpMethod.Post, url);
            forwardRequest.Content = new StringContent(chatRequestBody ?? "{}", Encoding.UTF8, "application/json");
            return await _httpClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead);
        }

        /// <summary>
        /// Converts a Responses API request body to a Chat Completions request body.
        /// </summary>
        private string ConvertResponsesRequestToChatCompletions(string responsesRequestBody) {
            using (JsonDocument document = JsonDocument.Parse(responsesRequestBody)) {
                JsonElement root = document.RootElement;
                using (var stream = new MemoryStream())
                using (var writer = new Utf8JsonWriter(stream)) {
                    writer.WriteStartObject();

                    string model = root.TryGetProperty("model", out JsonElement modelElement) &&
                                   modelElement.ValueKind == JsonValueKind.String
                        ? modelElement.GetString()
                        : "lm-studio";
                    writer.WriteString("model", model);
                    writer.WriteBoolean("stream", true);

                    if (root.TryGetProperty("temperature", out JsonElement temperature))
                        WritePropertyIfSimple(writer, "temperature", temperature);
                    if (root.TryGetProperty("max_output_tokens", out JsonElement maxOutputTokens) &&
                        maxOutputTokens.ValueKind == JsonValueKind.Number)
                        writer.WriteNumber("max_completion_tokens", maxOutputTokens.GetInt32());
                    if (root.TryGetProperty("tool_choice", out JsonElement toolChoice))
                        WritePropertyIfAny(writer, "tool_choice", toolChoice);

                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    if (root.TryGetProperty("instructions", out JsonElement instructionsElement) &&
                        instructionsElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(instructionsElement.GetString())) {
                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", instructionsElement.GetString());
                        writer.WriteEndObject();
                    }

                    if (root.TryGetProperty("input", out JsonElement inputElement))
                        WriteResponsesInputAsChatMessages(writer, inputElement);
                    writer.WriteEndArray();

                    if (root.TryGetProperty("tools", out JsonElement toolsElement) &&
                        toolsElement.ValueKind == JsonValueKind.Array) {
                        writer.WritePropertyName("tools");
                        writer.WriteStartArray();
                        foreach (JsonElement tool in toolsElement.EnumerateArray())
                            WriteResponsesToolAsChatTool(writer, tool);
                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                    writer.Flush();
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }

        /// <summary>
        /// Writes Responses input items as chat-completions messages.
        /// </summary>
        private void WriteResponsesInputAsChatMessages(Utf8JsonWriter writer, JsonElement inputElement) {
            if (inputElement.ValueKind == JsonValueKind.String) {
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", inputElement.GetString());
                writer.WriteEndObject();
                return;
            }

            if (inputElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement item in inputElement.EnumerateArray()) {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string type = item.TryGetProperty("type", out JsonElement typeElement) &&
                              typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : "";

                if (type.Equals("function_call_output", StringComparison.OrdinalIgnoreCase)) {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", ExtractStringProperty(item, "call_id") ?? ExtractStringProperty(item, "id") ?? "call_missing");
                    writer.WriteString("content", ExtractStringProperty(item, "output") ?? "");
                    writer.WriteEndObject();
                    continue;
                }

                if (type.Equals("function_call", StringComparison.OrdinalIgnoreCase)) {
                    writer.WriteStartObject();
                    writer.WriteString("role", "assistant");
                    writer.WritePropertyName("tool_calls");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("id", ExtractStringProperty(item, "call_id") ?? ExtractStringProperty(item, "id") ?? "call_missing");
                    writer.WriteString("type", "function");
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", ExtractStringProperty(item, "name") ?? "");
                    writer.WriteString("arguments", ExtractStringProperty(item, "arguments") ?? "{}");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    continue;
                }

                string role = ExtractStringProperty(item, "role");
                if (string.IsNullOrWhiteSpace(role))
                    role = "user";

                string content = item.TryGetProperty("content", out JsonElement contentElement)
                    ? ExtractResponsesContentText(contentElement)
                    : ExtractStringProperty(item, "text") ?? "";

                writer.WriteStartObject();
                writer.WriteString("role", role);
                writer.WriteString("content", content);
                writer.WriteEndObject();
            }
        }

        /// <summary>
        /// Writes a Responses function tool in Chat Completions tool schema.
        /// </summary>
        private void WriteResponsesToolAsChatTool(Utf8JsonWriter writer, JsonElement tool) {
            if (tool.ValueKind != JsonValueKind.Object) {
                tool.WriteTo(writer);
                return;
            }

            if (tool.TryGetProperty("function", out JsonElement existingFunction)) {
                tool.WriteTo(writer);
                return;
            }

            string type = ExtractStringProperty(tool, "type") ?? "function";
            if (!type.Equals("function", StringComparison.OrdinalIgnoreCase)) {
                tool.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WritePropertyName("function");
            writer.WriteStartObject();
            writer.WriteString("name", ExtractStringProperty(tool, "name") ?? "");
            writer.WriteString("description", ExtractStringProperty(tool, "description") ?? "");
            writer.WritePropertyName("parameters");
            if (tool.TryGetProperty("parameters", out JsonElement parameters))
                parameters.WriteTo(writer);
            else {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        /// <summary>
        /// Extracts text from Responses content arrays.
        /// </summary>
        private string ExtractResponsesContentText(JsonElement contentElement) {
            if (contentElement.ValueKind == JsonValueKind.String)
                return contentElement.GetString();

            if (contentElement.ValueKind != JsonValueKind.Array)
                return contentElement.ToString();

            var sb = new StringBuilder();
            foreach (JsonElement part in contentElement.EnumerateArray()) {
                if (part.ValueKind == JsonValueKind.String) {
                    sb.Append(part.GetString());
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                string text = ExtractStringProperty(part, "text") ??
                              ExtractStringProperty(part, "content") ??
                              ExtractStringProperty(part, "input_text") ??
                              ExtractStringProperty(part, "output_text");
                if (!string.IsNullOrEmpty(text))
                    sb.Append(text);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Extracts a string property from a JSON object.
        /// </summary>
        private string ExtractStringProperty(JsonElement element, string propertyName) {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(propertyName, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        /// <summary>
        /// Writes a primitive JSON property when supported.
        /// </summary>
        private void WritePropertyIfSimple(Utf8JsonWriter writer, string propertyName, JsonElement value) {
            if (value.ValueKind == JsonValueKind.Number) {
                writer.WritePropertyName(propertyName);
                value.WriteTo(writer);
            } else if (value.ValueKind == JsonValueKind.String) {
                writer.WriteString(propertyName, value.GetString());
            } else if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) {
                writer.WriteBoolean(propertyName, value.GetBoolean());
            }
        }

        /// <summary>
        /// Writes any JSON property value.
        /// </summary>
        private void WritePropertyIfAny(Utf8JsonWriter writer, string propertyName, JsonElement value) {
            writer.WritePropertyName(propertyName);
            value.WriteTo(writer);
        }

        /// <summary>
        /// Streams chat completions with early SSE keepalives so slow prompt processing does not look idle to VS.
        /// </summary>
        private async Task ForwardStreamingChatCompletionWithKeepAliveAsync(HttpListenerRequest req, string requestBody, HttpListenerResponse resp) {
            resp.StatusCode = 200;
            resp.ContentType = "text/event-stream; charset=utf-8";
            resp.SendChunked = true;
            resp.Headers["Cache-Control"] = "no-cache";
            resp.Headers["X-Accel-Buffering"] = "no";

            Task<HttpResponseMessage> upstreamTask = ForwardRawRequestToLmStudioAsync(req, requestBody);

            try {
                await WriteSseCommentAsync(resp.OutputStream, "proxy keepalive");

                while (!upstreamTask.IsCompleted) {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    await WriteSseCommentAsync(resp.OutputStream, "proxy keepalive");
                }

                using (HttpResponseMessage upstream = await upstreamTask) {
                    if (upstream == null) {
                        LogMessage("[Streaming] LM Studio returned no response.");
                        await WriteSseErrorAsync(resp.OutputStream, "Failed to get response from LM Studio.");
                        return;
                    }

                    LogMessage($"[Streaming] LM Studio response {(int)upstream.StatusCode} {upstream.ReasonPhrase} for {req.HttpMethod} {req.Url.AbsolutePath}");

                    if ((int)upstream.StatusCode >= 400) {
                        string errorBody = upstream.Content == null ? "" : await upstream.Content.ReadAsStringAsync();
                        LogMessage($"[Streaming] Upstream error body: {TruncateForLog(errorBody, 800)}");
                        await WriteSseErrorAsync(resp.OutputStream, string.IsNullOrWhiteSpace(errorBody) ? upstream.ReasonPhrase : errorBody);
                        return;
                    }

                    if (upstream.Content != null)
                        await StreamAdaptedChatCompletionsToClientAsync(upstream.Content, resp.OutputStream, requestBody);
                }
            } catch (Exception ex) {
                if (IsClientDisconnectedException(ex)) {
                    LogMessage("[Streaming] Client disconnected before proxy finished streaming; abandoning response.");
                    return;
                }

                LogMessage($"[Streaming] Proxy streaming error: {ex}");
                try {
                    await WriteSseErrorAsync(resp.OutputStream, "Proxy streaming error: " + ex.Message);
                } catch { }
            } finally {
                try {
                    resp.Close();
                } catch { }
            }
        }

        /// <summary>
        /// Writes an SSE comment frame.
        /// </summary>
        private async Task WriteSseCommentAsync(Stream stream, string comment) {
            byte[] bytes = Encoding.UTF8.GetBytes(": " + comment + "\n\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        /// <summary>
        /// Writes an OpenAI-shaped SSE error and terminates the stream.
        /// </summary>
        private async Task WriteSseErrorAsync(Stream stream, string errorMessage) {
            LogMessage($"[Streaming] Sending SSE error: {errorMessage}");
            string json = "{\"error\":{\"message\":\"" + EscapeJson(errorMessage) + "\",\"type\":\"proxy_error\"}}";
            byte[] bytes = Encoding.UTF8.GetBytes("data: " + json + "\n\ndata: [DONE]\n\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        private async Task StreamAdaptedChatCompletionsToClientAsync(HttpContent upstreamContent, Stream clientStream, string requestBody) {
            var nativeBuilders = new Dictionary<int, ToolCallBuilder>();
            var qwenToolText = new StringBuilder();
            bool sawNativeToolCalls = false;
            bool sawQwenToolCalls = false;
            bool wroteDone = false;
            bool thinkingOpen = false;
            string model = ExtractJsonStringProperty(requestBody, "model");
            if (string.IsNullOrWhiteSpace(model))
                model = "lm-studio";

            using (var upstreamStream = await upstreamContent.ReadAsStreamAsync())
            using (var reader = new StreamReader(upstreamStream, Encoding.UTF8)) {
                string line;
                while ((line = await reader.ReadLineAsync()) != null) {
                    if (string.IsNullOrWhiteSpace(line)) {
                        continue;
                    }

                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) {
                        await WriteRawSseFrameAsync(clientStream, line);
                        continue;
                    }

                    string payload = line.Substring(5).Trim();
                    if (payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) {
                        break;
                    }

                    string safePayload = EscapeInvalidJsonStringControlCharacters(payload, out int escapedControlCharacters);
                    if (escapedControlCharacters > 0)
                        LogMessage($"[JSON Repair] Escaped {escapedControlCharacters} raw control character(s) in live streaming chunk.");

                    bool suppressRawFrame = false;
                    try {
                        using (JsonDocument document = JsonDocument.Parse(safePayload)) {
                            JsonElement root = document.RootElement;
                            if (root.TryGetProperty("model", out JsonElement modelElement) &&
                                modelElement.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(modelElement.GetString())) {
                                model = modelElement.GetString();
                            }

                            if (root.TryGetProperty("choices", out JsonElement choices) &&
                                choices.ValueKind == JsonValueKind.Array &&
                                choices.GetArrayLength() > 0) {
                                foreach (JsonElement choice in choices.EnumerateArray()) {
                                    if (choice.TryGetProperty("delta", out JsonElement delta) &&
                                        delta.ValueKind == JsonValueKind.Object) {
                                        if (delta.TryGetProperty("tool_calls", out JsonElement nativeToolCalls) &&
                                            nativeToolCalls.ValueKind == JsonValueKind.Array) {
                                            sawNativeToolCalls = true;
                                            suppressRawFrame = true;
                                            AccumulateNativeToolCallDeltas(nativeBuilders, nativeToolCalls);
                                        }

                                        string reasoning = ExtractReasoningText(delta);
                                        StringBuilder visibleDelta = null;
                                        if (!string.IsNullOrEmpty(reasoning)) {
                                            suppressRawFrame = true;
                                            visibleDelta = new StringBuilder();
                                            if (!thinkingOpen) {
                                                visibleDelta.Append("**Thinking**\n\n");
                                                thinkingOpen = true;
                                            }
                                            visibleDelta.Append(FormatVisibleReasoningText(reasoning));
                                        }

                                        if (delta.TryGetProperty("content", out JsonElement contentElement) &&
                                            contentElement.ValueKind == JsonValueKind.String) {
                                            string content = contentElement.GetString() ?? "";
                                            string passThroughContent = InterceptQwenToolCallContent(content, qwenToolText, ref sawQwenToolCalls);
                                            if (!string.Equals(passThroughContent, content, StringComparison.Ordinal)) {
                                                suppressRawFrame = true;
                                            }

                                            if ((visibleDelta != null || thinkingOpen) && !string.IsNullOrEmpty(passThroughContent)) {
                                                suppressRawFrame = true;
                                                if (visibleDelta == null)
                                                    visibleDelta = new StringBuilder();
                                                visibleDelta.Append("\n\n");
                                                thinkingOpen = false;
                                            }

                                            if (visibleDelta != null)
                                                visibleDelta.Append(passThroughContent);
                                            else if (!string.Equals(passThroughContent, content, StringComparison.Ordinal) &&
                                                     !string.IsNullOrEmpty(passThroughContent)) {
                                                string contentFrame = BuildSingleContentDeltaSse(passThroughContent, model);
                                                await WriteRawSseTextAsync(clientStream, contentFrame);
                                            }
                                        }

                                        if (visibleDelta != null && visibleDelta.Length > 0) {
                                            string contentFrame = BuildSingleContentDeltaSse(visibleDelta.ToString(), model);
                                            await WriteRawSseTextAsync(clientStream, contentFrame);
                                        }
                                    }

                                    if (choice.TryGetProperty("finish_reason", out JsonElement finishReason) &&
                                        finishReason.ValueKind == JsonValueKind.String &&
                                        finishReason.GetString().Equals("tool_calls", StringComparison.OrdinalIgnoreCase)) {
                                        suppressRawFrame = true;
                                    }
                                }
                            }
                        }
                    } catch (Exception ex) {
                        LogMessage("[Streaming] Live adapter could not parse chunk; passing through raw frame: " + ex.Message);
                    }

                    if (!suppressRawFrame)
                        await WriteRawSseFrameAsync(clientStream, "data: " + payload);
                }
            }

            if (thinkingOpen) {
                await WriteRawSseTextAsync(clientStream, BuildSingleContentDeltaSse("\n\n", model));
                thinkingOpen = false;
            }

            if (sawNativeToolCalls && nativeBuilders.Count > 0) {
                var toolCalls = BuildToolCallsFromBuilders(nativeBuilders, requestBody);
                if (toolCalls.Count > 0) {
                    LogToolCalls("live native repaired", toolCalls);
                    await WriteRawSseTextAsync(clientStream, BuildToolCallSseResponse(toolCalls, model));
                    wroteDone = true;
                }
            } else if (sawQwenToolCalls && qwenToolText.Length > 0) {
                var toolCalls = ExtractQwenToolCalls(qwenToolText.ToString());
                HashSet<string> availableTools = GetAvailableToolNames(requestBody);
                RemapSuppressedToolCalls(toolCalls, availableTools, requestBody);
                RedirectSearchLoopToCreateFile(toolCalls, requestBody);
                GuardUnsafeCreateFileTargets(toolCalls, requestBody);
                if (availableTools.Count > 0)
                    toolCalls.RemoveAll(toolCall => toolCall == null ||
                                                   string.IsNullOrWhiteSpace(toolCall.Name) ||
                                                   !availableTools.Contains(toolCall.Name));
                if (toolCalls.Count > 0) {
                    LogToolCalls("live qwen repaired", toolCalls);
                    await WriteRawSseTextAsync(clientStream, BuildToolCallSseResponse(toolCalls, model));
                    wroteDone = true;
                }
            }

            if (!wroteDone)
                await WriteRawSseFrameAsync(clientStream, "data: [DONE]");
        }

        private void AccumulateNativeToolCallDeltas(Dictionary<int, ToolCallBuilder> builders, JsonElement nativeToolCalls) {
            foreach (JsonElement toolCall in nativeToolCalls.EnumerateArray()) {
                int index = toolCall.TryGetProperty("index", out JsonElement indexElement) &&
                            indexElement.ValueKind == JsonValueKind.Number &&
                            indexElement.TryGetInt32(out int parsedIndex)
                    ? parsedIndex
                    : builders.Count;

                if (!builders.TryGetValue(index, out ToolCallBuilder builder)) {
                    builder = new ToolCallBuilder();
                    builders[index] = builder;
                }

                if (toolCall.TryGetProperty("id", out JsonElement idElement) &&
                    idElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(idElement.GetString())) {
                    builder.Id = idElement.GetString();
                }

                if (toolCall.TryGetProperty("function", out JsonElement function) &&
                    function.ValueKind == JsonValueKind.Object) {
                    if (function.TryGetProperty("name", out JsonElement nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(nameElement.GetString())) {
                        builder.Name = nameElement.GetString();
                    }

                    if (function.TryGetProperty("arguments", out JsonElement argumentsElement)) {
                        builder.Arguments.Append(argumentsElement.ValueKind == JsonValueKind.String
                            ? argumentsElement.GetString()
                            : argumentsElement.ToString());
                    }
                }
            }
        }

        private List<ToolCallData> BuildToolCallsFromBuilders(Dictionary<int, ToolCallBuilder> builders, string requestBody) {
            var toolCalls = new List<ToolCallData>();
            foreach (var kv in builders) {
                ToolCallBuilder builder = kv.Value;
                if (builder == null || string.IsNullOrWhiteSpace(builder.Name))
                    continue;

                string rawArguments = builder.Arguments.ToString();
                if (string.IsNullOrWhiteSpace(rawArguments))
                    rawArguments = "{}";

                string repairedArguments = RepairKnownToolArguments(builder.Name, rawArguments);
                toolCalls.Add(new ToolCallData {
                    Name = builder.Name,
                    ArgumentsJson = repairedArguments
                });
            }

            HashSet<string> availableTools = GetAvailableToolNames(requestBody);
            RemapSuppressedToolCalls(toolCalls, availableTools, requestBody);
            RedirectSearchLoopToCreateFile(toolCalls, requestBody);
            GuardUnsafeCreateFileTargets(toolCalls, requestBody);
            if (availableTools.Count > 0)
                toolCalls.RemoveAll(toolCall => toolCall == null ||
                                               string.IsNullOrWhiteSpace(toolCall.Name) ||
                                               !availableTools.Contains(toolCall.Name));
            return toolCalls;
        }

        private string InterceptQwenToolCallContent(string content, StringBuilder qwenToolText, ref bool sawQwenToolCalls) {
            if (string.IsNullOrEmpty(content))
                return content;

            if (sawQwenToolCalls) {
                qwenToolText.Append(content);
                return "";
            }

            int toolStart = content.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
            if (toolStart < 0)
                return content;

            sawQwenToolCalls = true;
            qwenToolText.Append(content.Substring(toolStart));
            return content.Substring(0, toolStart);
        }

        private string FormatVisibleReasoningText(string text) {
            if (string.IsNullOrEmpty(text))
                return "";

            string formatted = text.Replace("\r\n", "\n").Replace('\r', '\n');
            formatted = System.Text.RegularExpressions.Regex.Replace(formatted, @"(?<=[A-Za-z])(?=\d)", " ");
            formatted = System.Text.RegularExpressions.Regex.Replace(formatted, @"(?<=\d)(?=[A-Za-z])", " ");
            formatted = System.Text.RegularExpressions.Regex.Replace(formatted, @"(?<=[.!?])(?=[A-Z])", " ");
            formatted = System.Text.RegularExpressions.Regex.Replace(formatted, @"(?<!\n)(?<=:)(?=\d+\.)", "\n");
            formatted = System.Text.RegularExpressions.Regex.Replace(formatted, @"(?<!\n)(?<=\s)(?=\d+\.\s)", "\n");
            formatted = System.Text.RegularExpressions.Regex.Replace(formatted, @"\n{3,}", "\n\n");
            return formatted;
        }

        private string BuildSingleContentDeltaSse(string content, string model) {
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string completionId = "chatcmpl_" + Guid.NewGuid().ToString("N");
            return "data: {\"id\":\"" + completionId +
                   "\",\"object\":\"chat.completion.chunk\",\"created\":" + created.ToString() +
                   ",\"model\":\"" + EscapeJson(model ?? "lm-studio") +
                   "\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + EscapeJson(content ?? "") +
                   "\"},\"finish_reason\":null}]}\n\n";
        }

        private async Task WriteRawSseFrameAsync(Stream stream, string frameLine) {
            await WriteRawSseTextAsync(stream, frameLine + "\n\n");
        }

        private async Task WriteRawSseTextAsync(Stream stream, string text) {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        /// <summary>
        /// Returns true when the exception means the Visual Studio client closed the stream.
        /// </summary>
        private bool IsClientDisconnectedException(Exception ex) {
            while (ex != null) {
                if (ex is HttpListenerException listenerException) {
                    if (listenerException.ErrorCode == 64 ||
                        listenerException.ErrorCode == 995 ||
                        listenerException.ErrorCode == 1229)
                        return true;
                }

                if (ex is ObjectDisposedException)
                    return true;

                ex = ex.InnerException;
            }

            return false;
        }

        /// <summary>
        /// Writes a Responses-shaped SSE error and terminates the stream.
        /// </summary>
        private async Task WriteResponsesSseErrorAsync(Stream stream, string errorMessage) {
            LogMessage("[Responses Bridge] Sending SSE error: " + errorMessage);
            string responseId = "resp_" + Guid.NewGuid().ToString("N");
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string data = "{\"type\":\"response.failed\",\"response\":{\"id\":\"" + responseId +
                          "\",\"object\":\"response\",\"created_at\":" + created.ToString() +
                          ",\"status\":\"failed\",\"error\":{\"message\":\"" + EscapeJson(errorMessage) +
                          "\",\"type\":\"proxy_error\"}}}";
            byte[] bytes = Encoding.UTF8.GetBytes("event: response.failed\n" +
                                                  "data: " + data + "\n\n" +
                                                  "data: [DONE]\n\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        /// <summary>
        /// Removes an old guard fallback phrase that can get stuck in VS conversation history.
        /// </summary>
        private string ScrubDeadEndProjectContextMessage(string text) {
            if (string.IsNullOrEmpty(text))
                return text;

            const string deadEnd = "I have enough project context now. I will stop reading the project and continue from the existing file context.";
            if (!text.Contains(deadEnd))
                return text;

            const string replacement = "Continue by using exact file tools or editing from the current Visual Studio context; do not repeat get_projects_in_solution.";
            return text.Replace(deadEnd, replacement);
        }

        /// <summary>
        /// Escapes raw control characters that occasionally appear inside model/VS JSON strings.
        /// </summary>
        private string EscapeInvalidJsonStringControlCharacters(string json, out int escapedCount) {
            escapedCount = 0;
            if (string.IsNullOrEmpty(json))
                return json;

            StringBuilder sb = null;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < json.Length; i++) {
                char ch = json[i];
                string replacement = null;

                if (inString && !escaped && ch < 0x20) {
                    switch (ch) {
                        case '\b':
                            replacement = "\\b";
                            break;
                        case '\t':
                            replacement = "\\t";
                            break;
                        case '\n':
                            replacement = "\\n";
                            break;
                        case '\f':
                            replacement = "\\f";
                            break;
                        case '\r':
                            replacement = "\\r";
                            break;
                        default:
                            replacement = "\\u" + ((int)ch).ToString("x4");
                            break;
                    }
                }

                if (replacement != null) {
                    if (sb == null) {
                        sb = new StringBuilder(json.Length + 16);
                        sb.Append(json, 0, i);
                    }

                    sb.Append(replacement);
                    escapedCount++;
                    escaped = false;
                    continue;
                }

                if (sb != null)
                    sb.Append(ch);

                if (escaped) {
                    escaped = false;
                } else if (ch == '\\' && inString) {
                    escaped = true;
                } else if (ch == '"') {
                    inString = !inString;
                }
            }

            return sb == null ? json : sb.ToString();
        }

        /// <summary>
        /// Reads the upstream response body while continuing to emit SSE keepalives to the client.
        /// </summary>
        private async Task<string> ReadUpstreamBodyWithKeepAliveAsync(HttpContent content, Stream clientStream) {
            Task<string> readTask = content.ReadAsStringAsync();

            while (!readTask.IsCompleted) {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await WriteSseCommentAsync(clientStream, "proxy keepalive");
            }

            return await readTask;
        }

        /// <summary>
        /// Repairs native OpenAI streaming tool-call deltas from LM Studio before Visual Studio executes them.
        /// </summary>
        private string AdaptNativeOpenAiToolCallStream(string upstreamBody, string requestBody) {
            if (string.IsNullOrWhiteSpace(upstreamBody) || upstreamBody.Contains("<tool_call>"))
                return upstreamBody;

            var builders = new Dictionary<int, ToolCallBuilder>();
            string model = ExtractJsonStringProperty(requestBody, "model");
            if (string.IsNullOrWhiteSpace(model))
                model = "lm-studio";

            try {
                using (StringReader reader = new StringReader(upstreamBody)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        line = line.Trim();
                        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string data = line.Substring(5).Trim();
                        if (data.Length == 0 || data == "[DONE]")
                            continue;

                        string safeData = EscapeInvalidJsonStringControlCharacters(data, out int escapedControlCharacters);
                        if (escapedControlCharacters > 0)
                            LogMessage($"[JSON Repair] Escaped {escapedControlCharacters} raw control character(s) in native streaming chunk.");

                        using (JsonDocument document = JsonDocument.Parse(safeData)) {
                            if (document.RootElement.TryGetProperty("model", out JsonElement modelElement) &&
                                modelElement.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(modelElement.GetString())) {
                                model = modelElement.GetString();
                            }

                            if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                                choices.ValueKind != JsonValueKind.Array ||
                                choices.GetArrayLength() == 0)
                                continue;

                            JsonElement choice = choices[0];
                            if (!choice.TryGetProperty("delta", out JsonElement delta) ||
                                delta.ValueKind != JsonValueKind.Object ||
                                !delta.TryGetProperty("tool_calls", out JsonElement nativeToolCalls) ||
                                nativeToolCalls.ValueKind != JsonValueKind.Array)
                                continue;

                            foreach (JsonElement toolCall in nativeToolCalls.EnumerateArray()) {
                                int index = toolCall.TryGetProperty("index", out JsonElement indexElement) &&
                                            indexElement.ValueKind == JsonValueKind.Number &&
                                            indexElement.TryGetInt32(out int parsedIndex)
                                    ? parsedIndex
                                    : builders.Count;

                                if (!builders.TryGetValue(index, out ToolCallBuilder builder)) {
                                    builder = new ToolCallBuilder();
                                    builders[index] = builder;
                                }

                                if (toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                    idElement.ValueKind == JsonValueKind.String &&
                                    !string.IsNullOrWhiteSpace(idElement.GetString())) {
                                    builder.Id = idElement.GetString();
                                }

                                if (toolCall.TryGetProperty("function", out JsonElement function) &&
                                    function.ValueKind == JsonValueKind.Object) {
                                    if (function.TryGetProperty("name", out JsonElement nameElement) &&
                                        nameElement.ValueKind == JsonValueKind.String &&
                                        !string.IsNullOrWhiteSpace(nameElement.GetString())) {
                                        builder.Name = nameElement.GetString();
                                    }

                                    if (function.TryGetProperty("arguments", out JsonElement argumentsElement)) {
                                        string argumentsChunk = argumentsElement.ValueKind == JsonValueKind.String
                                            ? argumentsElement.GetString()
                                            : argumentsElement.GetRawText();
                                        builder.Arguments.Append(argumentsChunk);
                                    }
                                }
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                LogMessage("[Native Tool Calls] Error parsing streaming tool calls: " + ex.Message);
                return upstreamBody;
            }

            if (builders.Count == 0)
                return upstreamBody;

            var toolCalls = new List<ToolCallData>();
            foreach (var kv in builders) {
                ToolCallBuilder builder = kv.Value;
                if (builder == null || string.IsNullOrWhiteSpace(builder.Name))
                    continue;

                string rawArguments = builder.Arguments.ToString();
                if (string.IsNullOrWhiteSpace(rawArguments))
                    rawArguments = "{}";

                string repairedArguments = RepairKnownToolArguments(builder.Name, rawArguments);
                if (!string.Equals(rawArguments, repairedArguments, StringComparison.Ordinal)) {
                    LogMessage("[Native Tool Args Repair] " + builder.Name + ": " +
                               TruncateForLog(rawArguments, 300) + " -> " +
                               TruncateForLog(repairedArguments, 300));
                }

                toolCalls.Add(new ToolCallData {
                    Name = builder.Name,
                    ArgumentsJson = repairedArguments
                });
            }

            if (toolCalls.Count == 0)
                return upstreamBody;

            HashSet<string> availableTools = GetAvailableToolNames(requestBody);
            RemapSuppressedToolCalls(toolCalls, availableTools, requestBody);
            RedirectSearchLoopToCreateFile(toolCalls, requestBody);
            GuardUnsafeCreateFileTargets(toolCalls, requestBody);
            LogToolCalls("native repaired", toolCalls);
            return BuildToolCallSseResponse(toolCalls, model);
        }

        /// <summary>
        /// Converts Qwen raw &lt;tool_call&gt; content into OpenAI streaming tool_call deltas.
        /// </summary>
        private string AdaptQwenToolCallStream(string upstreamBody, string requestBody) {
            if (string.IsNullOrEmpty(upstreamBody) || !upstreamBody.Contains("<tool_call>"))
                return upstreamBody;

            string content = ExtractStreamedAssistantContent(upstreamBody);
            if (string.IsNullOrWhiteSpace(content) || !content.Contains("<tool_call>"))
                return upstreamBody;

            var toolCalls = ExtractQwenToolCalls(content);
            if (toolCalls.Count == 0)
                return upstreamBody;

            string model = ExtractJsonStringProperty(requestBody, "model");
            if (string.IsNullOrWhiteSpace(model))
                model = "lm-studio";

            if (IsToolChoiceNone(requestBody))
                return BuildTextOnlySseResponse(SanitizeAssistantText(StripQwenToolCalls(content, requestBody)), model);

            HashSet<string> availableTools = GetAvailableToolNames(requestBody);
            if (availableTools.Count > 0)
                LogMessage("[Tool Calls] Available tools: " + TruncateForLog(string.Join(", ", availableTools), 900));
            LogToolCalls("parsed", toolCalls);
            RemapSuppressedToolCalls(toolCalls, availableTools, requestBody);
            RedirectSearchLoopToCreateFile(toolCalls, requestBody);
            GuardUnsafeCreateFileTargets(toolCalls, requestBody);
            LogToolCalls("after remap", toolCalls);
            if (availableTools.Count > 0) {
                var droppedTools = new List<string>();
                foreach (ToolCallData toolCall in toolCalls) {
                    if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.Name))
                        droppedTools.Add("<missing>");
                    else if (!availableTools.Contains(toolCall.Name))
                        droppedTools.Add(toolCall.Name);
                }

                toolCalls.RemoveAll(toolCall => toolCall == null ||
                                               string.IsNullOrWhiteSpace(toolCall.Name) ||
                                               !availableTools.Contains(toolCall.Name));

                if (droppedTools.Count > 0)
                    LogMessage("[Tool Calls] Dropped unavailable tool call(s): " + string.Join(", ", droppedTools));
            }
            LogToolCalls("after availability filter", toolCalls);

            if (toolCalls.Count == 0) {
                LogMessage("[Tool Calls] No executable tool calls after adaptation; returning text-only response.");
                return BuildTextOnlySseResponse(SanitizeAssistantText(StripQwenToolCalls(content, requestBody)), model);
            }

            return BuildToolCallSseResponse(toolCalls, model);
        }

        /// <summary>
        /// Prevents a text-only ending immediately after build/get_errors failures by requesting the failing file.
        /// </summary>
        private string AdaptTextOnlyRepairContinuation(string upstreamBody, string requestBody) {
            if (string.IsNullOrWhiteSpace(upstreamBody) ||
                upstreamBody.Contains("\"tool_calls\"", StringComparison.OrdinalIgnoreCase) ||
                IsToolChoiceNone(requestBody) ||
                !IsToolAdvertised(requestBody, "get_file"))
                return upstreamBody;

            if (!HasUnfixedBuildOrErrorFailure(requestBody, out string errorFile) ||
                string.IsNullOrWhiteSpace(errorFile))
                return upstreamBody;

            if (CountToolCallsAfterLatestBuildFailure(requestBody, "get_file", errorFile) >= 2)
                return upstreamBody;

            string content = ExtractStreamedAssistantContent(upstreamBody);
            if (string.IsNullOrWhiteSpace(content))
                return upstreamBody;

            string model = ExtractJsonStringProperty(requestBody, "model");
            if (string.IsNullOrWhiteSpace(model))
                model = "lm-studio";

            var toolCalls = new List<ToolCallData> {
                new ToolCallData {
                    Name = "get_file",
                    ArgumentsJson = "{\"filename\":\"" + EscapeJson(errorFile) + "\",\"startLine\":1,\"endLine\":120,\"includeLineNumbers\":true}"
                }
            };

            LogMessage("[Build Error Continuation] Replaced text-only response after build/get_errors failure with get_file for " + errorFile + ".");
            return BuildToolCallSseResponse(toolCalls, model);
        }

        /// <summary>
        /// Builds a compact OpenAI-compatible SSE response containing repaired tool calls.
        /// </summary>
        private string BuildToolCallSseResponse(List<ToolCallData> toolCalls, string model) {
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string completionId = "chatcmpl_" + Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();

            sb.Append("data: {\"id\":\"").Append(completionId)
              .Append("\",\"object\":\"chat.completion.chunk\",\"created\":").Append(created)
              .Append(",\"model\":\"").Append(EscapeJson(model ?? "lm-studio"))
              .Append("\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n");

            for (int i = 0; i < toolCalls.Count; i++) {
                ToolCallData toolCall = toolCalls[i];
                string toolCallId = "call_" + Guid.NewGuid().ToString("N").Substring(0, 16);

                sb.Append("data: {\"id\":\"").Append(completionId)
                  .Append("\",\"object\":\"chat.completion.chunk\",\"created\":").Append(created)
                  .Append(",\"model\":\"").Append(EscapeJson(model ?? "lm-studio"))
                  .Append("\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":")
                  .Append(i)
                  .Append(",\"id\":\"").Append(toolCallId)
                  .Append("\",\"type\":\"function\",\"function\":{\"name\":\"")
                  .Append(EscapeJson(toolCall.Name))
                  .Append("\",\"arguments\":\"")
                  .Append(EscapeJson(toolCall.ArgumentsJson))
                  .Append("\"}}]},\"finish_reason\":null}]}\n\n");
            }

            sb.Append("data: {\"id\":\"").Append(completionId)
              .Append("\",\"object\":\"chat.completion.chunk\",\"created\":").Append(created)
              .Append(",\"model\":\"").Append(EscapeJson(model ?? "lm-studio"))
              .Append("\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
            sb.Append("data: [DONE]\n\n");

            return sb.ToString();
        }

        /// <summary>
        /// Converts Chat Completions SSE frames to Responses API SSE frames.
        /// </summary>
        private string ConvertChatSseToResponsesSse(string chatSse, string chatRequestBody) {
            string model = ExtractJsonStringProperty(chatRequestBody, "model");
            if (string.IsNullOrWhiteSpace(model))
                model = "lm-studio";

            string responseId = "resp_" + Guid.NewGuid().ToString("N");
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var text = new StringBuilder();
            var builders = new Dictionary<int, ToolCallBuilder>();

            using (StringReader reader = new StringReader(chatSse ?? "")) {
                string line;
                while ((line = reader.ReadLine()) != null) {
                    line = line.Trim();
                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string data = line.Substring(5).Trim();
                    if (data.Length == 0 || data == "[DONE]")
                        continue;

                    try {
                        string safeData = EscapeInvalidJsonStringControlCharacters(data, out _);
                        using (JsonDocument document = JsonDocument.Parse(safeData)) {
                            if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                                choices.ValueKind != JsonValueKind.Array ||
                                choices.GetArrayLength() == 0)
                                continue;

                            JsonElement choice = choices[0];
                            if (!choice.TryGetProperty("delta", out JsonElement delta) ||
                                delta.ValueKind != JsonValueKind.Object)
                                continue;

                            if (delta.TryGetProperty("content", out JsonElement contentElement) &&
                                contentElement.ValueKind == JsonValueKind.String)
                                text.Append(contentElement.GetString());

                            if (delta.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                                toolCalls.ValueKind == JsonValueKind.Array) {
                                foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                                    int index = toolCall.TryGetProperty("index", out JsonElement indexElement) &&
                                                indexElement.ValueKind == JsonValueKind.Number &&
                                                indexElement.TryGetInt32(out int parsedIndex)
                                        ? parsedIndex
                                        : builders.Count;

                                    if (!builders.TryGetValue(index, out ToolCallBuilder builder)) {
                                        builder = new ToolCallBuilder();
                                        builders[index] = builder;
                                    }

                                    if (toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                        idElement.ValueKind == JsonValueKind.String)
                                        builder.Id = idElement.GetString();

                                    if (toolCall.TryGetProperty("function", out JsonElement function) &&
                                        function.ValueKind == JsonValueKind.Object) {
                                        if (function.TryGetProperty("name", out JsonElement nameElement) &&
                                            nameElement.ValueKind == JsonValueKind.String)
                                            builder.Name = nameElement.GetString();
                                        if (function.TryGetProperty("arguments", out JsonElement argumentsElement)) {
                                            string chunk = argumentsElement.ValueKind == JsonValueKind.String
                                                ? argumentsElement.GetString()
                                                : argumentsElement.GetRawText();
                                            builder.Arguments.Append(chunk);
                                        }
                                    }
                                }
                            }
                        }
                    } catch (Exception ex) {
                        LogMessage("[Responses Bridge] Could not parse chat SSE frame: " + ex.Message);
                    }
                }
            }

            if (builders.Count > 0)
                return BuildResponsesToolCallSse(responseId, created, model, builders);

            return BuildResponsesTextSse(responseId, created, model, text.ToString());
        }

        /// <summary>
        /// Builds Responses API SSE frames for text output.
        /// </summary>
        private string BuildResponsesTextSse(string responseId, long created, string model, string content) {
            string itemId = "msg_" + Guid.NewGuid().ToString("N");
            string safeContent = EscapeJson(content ?? "");
            var sb = new StringBuilder();

            sb.Append("event: response.created\n");
            sb.Append("data: {\"type\":\"response.created\",\"response\":{\"id\":\"").Append(responseId)
              .Append("\",\"object\":\"response\",\"created_at\":").Append(created)
              .Append(",\"status\":\"in_progress\",\"model\":\"").Append(EscapeJson(model))
              .Append("\",\"output\":[]}}\n\n");

            sb.Append("event: response.output_item.added\n");
            sb.Append("data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"").Append(itemId)
              .Append("\",\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"assistant\",\"content\":[]}}\n\n");

            sb.Append("event: response.content_part.added\n");
            sb.Append("data: {\"type\":\"response.content_part.added\",\"item_id\":\"").Append(itemId)
              .Append("\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\"}}\n\n");

            if (!string.IsNullOrEmpty(content)) {
                sb.Append("event: response.output_text.delta\n");
                sb.Append("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"").Append(itemId)
                  .Append("\",\"output_index\":0,\"content_index\":0,\"delta\":\"").Append(safeContent).Append("\"}\n\n");
            }

            sb.Append("event: response.output_text.done\n");
            sb.Append("data: {\"type\":\"response.output_text.done\",\"item_id\":\"").Append(itemId)
              .Append("\",\"output_index\":0,\"content_index\":0,\"text\":\"").Append(safeContent).Append("\"}\n\n");

            sb.Append("event: response.content_part.done\n");
            sb.Append("data: {\"type\":\"response.content_part.done\",\"item_id\":\"").Append(itemId)
              .Append("\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"").Append(safeContent).Append("\"}}\n\n");

            sb.Append("event: response.output_item.done\n");
            sb.Append("data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"").Append(itemId)
              .Append("\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"").Append(safeContent).Append("\"}]}}\n\n");

            sb.Append("event: response.completed\n");
            sb.Append("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"").Append(responseId)
              .Append("\",\"object\":\"response\",\"created_at\":").Append(created)
              .Append(",\"status\":\"completed\",\"model\":\"").Append(EscapeJson(model))
              .Append("\",\"output\":[{\"id\":\"").Append(itemId)
              .Append("\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"").Append(safeContent).Append("\"}]}]}}\n\n");
            sb.Append("data: [DONE]\n\n");
            return sb.ToString();
        }

        /// <summary>
        /// Builds Responses API SSE frames for function-call output.
        /// </summary>
        private string BuildResponsesToolCallSse(string responseId, long created, string model, Dictionary<int, ToolCallBuilder> builders) {
            var sb = new StringBuilder();
            sb.Append("event: response.created\n");
            sb.Append("data: {\"type\":\"response.created\",\"response\":{\"id\":\"").Append(responseId)
              .Append("\",\"object\":\"response\",\"created_at\":").Append(created)
              .Append(",\"status\":\"in_progress\",\"model\":\"").Append(EscapeJson(model))
              .Append("\",\"output\":[]}}\n\n");

            int outputIndex = 0;
            foreach (var kv in builders) {
                ToolCallBuilder builder = kv.Value;
                if (builder == null || string.IsNullOrWhiteSpace(builder.Name))
                    continue;

                string itemId = "fc_" + Guid.NewGuid().ToString("N");
                string callId = string.IsNullOrWhiteSpace(builder.Id) ? "call_" + Guid.NewGuid().ToString("N").Substring(0, 16) : builder.Id;
                string arguments = builder.Arguments.ToString();

                sb.Append("event: response.output_item.added\n");
                sb.Append("data: {\"type\":\"response.output_item.added\",\"output_index\":").Append(outputIndex)
                  .Append(",\"item\":{\"id\":\"").Append(itemId)
                  .Append("\",\"type\":\"function_call\",\"status\":\"in_progress\",\"call_id\":\"").Append(EscapeJson(callId))
                  .Append("\",\"name\":\"").Append(EscapeJson(builder.Name))
                  .Append("\",\"arguments\":\"\"}}\n\n");

                sb.Append("event: response.function_call_arguments.delta\n");
                sb.Append("data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"").Append(itemId)
                  .Append("\",\"output_index\":").Append(outputIndex)
                  .Append(",\"delta\":\"").Append(EscapeJson(arguments)).Append("\"}\n\n");

                sb.Append("event: response.function_call_arguments.done\n");
                sb.Append("data: {\"type\":\"response.function_call_arguments.done\",\"item_id\":\"").Append(itemId)
                  .Append("\",\"output_index\":").Append(outputIndex)
                  .Append(",\"arguments\":\"").Append(EscapeJson(arguments)).Append("\"}\n\n");

                sb.Append("event: response.output_item.done\n");
                sb.Append("data: {\"type\":\"response.output_item.done\",\"output_index\":").Append(outputIndex)
                  .Append(",\"item\":{\"id\":\"").Append(itemId)
                  .Append("\",\"type\":\"function_call\",\"status\":\"completed\",\"call_id\":\"").Append(EscapeJson(callId))
                  .Append("\",\"name\":\"").Append(EscapeJson(builder.Name))
                  .Append("\",\"arguments\":\"").Append(EscapeJson(arguments)).Append("\"}}\n\n");

                outputIndex++;
            }

            sb.Append("event: response.completed\n");
            sb.Append("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"").Append(responseId)
              .Append("\",\"object\":\"response\",\"created_at\":").Append(created)
              .Append(",\"status\":\"completed\",\"model\":\"").Append(EscapeJson(model))
              .Append("\",\"output\":[]}}\n\n");
            sb.Append("data: [DONE]\n\n");
            return sb.ToString();
        }

        /// <summary>
        /// Extracts text content from OpenAI-compatible streaming chunks.
        /// </summary>
        private string ExtractStreamedAssistantContent(string upstreamBody) {
            var sb = new StringBuilder();
            using (StringReader reader = new StringReader(upstreamBody)) {
                string line;
                while ((line = reader.ReadLine()) != null) {
                    line = line.Trim();
                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string data = line.Substring(5).Trim();
                    if (data == "[DONE]" || data.Length == 0)
                        continue;

                    try {
                        string safeData = EscapeInvalidJsonStringControlCharacters(data, out int escapedControlCharacters);
                        if (escapedControlCharacters > 0)
                            LogMessage($"[JSON Repair] Escaped {escapedControlCharacters} raw control character(s) in assistant content chunk.");

                        using (JsonDocument document = JsonDocument.Parse(safeData)) {
                            if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                                choices.ValueKind != JsonValueKind.Array ||
                                choices.GetArrayLength() == 0)
                                continue;

                            JsonElement choice = choices[0];
                            if (choice.TryGetProperty("delta", out JsonElement delta) &&
                                delta.ValueKind == JsonValueKind.Object &&
                                delta.TryGetProperty("content", out JsonElement deltaContent) &&
                                deltaContent.ValueKind == JsonValueKind.String) {
                                sb.Append(deltaContent.GetString());
                            } else if (choice.TryGetProperty("message", out JsonElement message) &&
                                       message.ValueKind == JsonValueKind.Object &&
                                       message.TryGetProperty("content", out JsonElement messageContent) &&
                                       messageContent.ValueKind == JsonValueKind.String) {
                                sb.Append(messageContent.GetString());
                            }
                        }
                    } catch {
                        // Keep raw passthrough behavior if a non-JSON event appears.
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Extracts Qwen-style tool calls from assistant text.
        /// </summary>
        private List<ToolCallData> ExtractQwenToolCalls(string content) {
            var result = new List<ToolCallData>();
            int searchStart = 0;

            while (searchStart < content.Length) {
                int start = content.IndexOf("<tool_call>", searchStart, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                    break;

                start += "<tool_call>".Length;
                int end = content.IndexOf("</tool_call>", start, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                    break;

                string json = content.Substring(start, end - start).Trim();
                ToolCallData parsed = ParseQwenToolCall(json);
                if (parsed != null)
                    result.Add(parsed);

                searchStart = end + "</tool_call>".Length;
            }

            return result;
        }

        /// <summary>
        /// Logs concise tool-call diagnostics without dumping the full prompt.
        /// </summary>
        private void LogToolCalls(string stage, List<ToolCallData> toolCalls) {
                try {
                    if (toolCalls == null || toolCalls.Count == 0) {
                        LogMessage("[Tool Calls] [" + stage + "] none");
                        return;
                    }

                var parts = new List<string>();
                foreach (ToolCallData toolCall in toolCalls) {
                    if (toolCall == null)
                        continue;

                    string args = toolCall.ArgumentsJson ?? "";
                    if (args.Length > 180)
                        args = args.Substring(0, 180) + "...";

                    parts.Add(toolCall.Name + "(" + args + ")");
                }

                LogMessage("[Tool Calls] [" + stage + "] " + string.Join("; ", parts));
            } catch { }
        }

        /// <summary>
        /// Returns true when the request explicitly forbids tool calls.
        /// </summary>
        private bool IsToolChoiceNone(string requestBody) {
            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    return document.RootElement.TryGetProperty("tool_choice", out JsonElement toolChoice) &&
                           toolChoice.ValueKind == JsonValueKind.String &&
                           toolChoice.GetString().Equals("none", StringComparison.OrdinalIgnoreCase);
                }
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Gets tool names still advertised to LM Studio in the current request.
        /// </summary>
        private HashSet<string> GetAvailableToolNames(string requestBody) {
            var result = new HashSet<string>(StringComparer.Ordinal);

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("tools", out JsonElement tools) ||
                        tools.ValueKind != JsonValueKind.Array)
                        return result;

                    foreach (JsonElement tool in tools.EnumerateArray()) {
                        if (tool.TryGetProperty("function", out JsonElement function) &&
                            function.ValueKind == JsonValueKind.Object &&
                            function.TryGetProperty("name", out JsonElement nameElement) &&
                            nameElement.ValueKind == JsonValueKind.String) {
                            string name = nameElement.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                                result.Add(name);
                        }
                    }
                }
            } catch { }

            return result;
        }

        /// <summary>
        /// Redirects repeated low-value tool calls to a more specific next tool when possible.
        /// </summary>
        private void RemapSuppressedToolCalls(List<ToolCallData> toolCalls, HashSet<string> availableTools, string requestBody) {
            if (toolCalls == null || toolCalls.Count == 0 || availableTools == null || availableTools.Count == 0)
                return;

            int historicalProjectCalls = CountHistoricalToolCalls(requestBody, "get_projects_in_solution");
            string projectPath = ExtractProjectPathFromToolResult(ExtractFirstToolResultForTool(requestBody, "get_projects_in_solution"));

            foreach (ToolCallData toolCall in toolCalls) {
                if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.Name))
                    continue;

                if (toolCall.Name.Equals("get_projects_in_solution", StringComparison.Ordinal) &&
                    availableTools.Contains("get_files_in_project") &&
                    historicalProjectCalls >= 1 &&
                    !string.IsNullOrWhiteSpace(projectPath)) {
                    toolCall.Name = "get_files_in_project";
                    toolCall.ArgumentsJson = "{\"projectPath\":\"" + EscapeJson(projectPath.Trim()) + "\"}";
                    LogMessage("[Tool Loop Redirect] Rewrote repeated get_projects_in_solution to get_files_in_project for " + projectPath.Trim());
                }
            }
        }

        /// <summary>
        /// Redirects repeated search/navigation loops for a missing creation target into create_file.
        /// </summary>
        private void RedirectSearchLoopToCreateFile(List<ToolCallData> toolCalls, string requestBody) {
            if (toolCalls == null || toolCalls.Count == 0 || string.IsNullOrWhiteSpace(requestBody))
                return;

            if (!HasCreateFileGoal(requestBody) || !IsToolAdvertised(requestBody, "create_file"))
                return;

            string targetPath = InferMissingCreationTargetPath(requestBody);
            if (string.IsNullOrWhiteSpace(targetPath))
                return;

            if (!IsSafeCreateFileTarget(targetPath)) {
                LogMessage("[Create File Redirect] Refusing unsafe create_file target: " + targetPath);
                return;
            }

            if (HasSuccessfulFileRead(requestBody, targetPath)) {
                LogMessage("[Create File Redirect] Refusing to create existing file already read successfully: " + targetPath);
                return;
            }

            if (HasRepeatedCreateFileAttempts(requestBody, targetPath, 3)) {
                LogMessage("[Create File Redirect] Refusing to repeat create_file for " + targetPath + " because it was already attempted repeatedly.");
                return;
            }

            int repeatedSearchCount = CountRepeatedSearchOrNavigationAttempts(requestBody, targetPath);
            if (repeatedSearchCount < 4)
                return;

            foreach (ToolCallData toolCall in toolCalls) {
                if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.Name))
                    continue;

                if (!IsSearchOrNavigationTool(toolCall.Name))
                    continue;

                toolCall.Name = "create_file";
                toolCall.ArgumentsJson = BuildCreateFileArguments(targetPath, null);
                LogMessage("[Create File Redirect] Rewrote repeated " + repeatedSearchCount.ToString() +
                           " search/navigation attempt(s) to create_file for " + targetPath);
                return;
            }
        }

        /// <summary>
        /// Infers the file the model is failing to find during a create-file task.
        /// </summary>
        private string InferMissingCreationTargetPath(string requestBody) {
            string requested = ExtractCreationTargetFileName(requestBody);
            if (!string.IsNullOrWhiteSpace(requested)) {
                requested = RepairPathControlCharacters(requested.Trim());
                if (requested.IndexOf('\\') >= 0 || requested.IndexOf('/') >= 0)
                    return requested;

                string projectFolder = ExtractProjectFolderFromConversation(requestBody);
                if (string.IsNullOrWhiteSpace(projectFolder))
                    projectFolder = "Maf Scale";
                return projectFolder.TrimEnd('\\', '/') + "\\" + requested;
            }

            string lastUserMessage = ExtractLastUserMessage(requestBody) ?? "";
            string lower = lastUserMessage.ToLowerInvariant();
            if (lower.Contains("fkkvs")) {
                string projectFolder = ExtractProjectFolderFromConversation(requestBody);
                if (string.IsNullOrWhiteSpace(projectFolder))
                    projectFolder = "Maf Scale";
                return projectFolder.TrimEnd('\\', '/') + "\\fkkvs.xaml.vb";
            }

            return null;
        }

        /// <summary>
        /// Gets the most recent explicit missing/new file target used in search/create calls.
        /// </summary>
        private string ExtractCreationTargetFileName(string requestBody) {
            string result = null;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return null;

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        if (!message.TryGetProperty("role", out JsonElement roleElement) ||
                            roleElement.ValueKind != JsonValueKind.String ||
                            !roleElement.GetString().Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                            !message.TryGetProperty("tool_calls", out JsonElement toolCalls) ||
                            toolCalls.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                                function.ValueKind != JsonValueKind.Object)
                                continue;

                            string name = ExtractStringProperty(function, "name") ?? "";
                            string arguments = function.TryGetProperty("arguments", out JsonElement argumentsElement)
                                ? argumentsElement.ToString()
                                : "";

                            string candidate = ExtractCreationTargetFromToolArguments(name, arguments);
                            if (!string.IsNullOrWhiteSpace(candidate))
                                result = candidate;
                        }
                    }
                }
            } catch { }

            return result;
        }

        /// <summary>
        /// Extracts a filename/path only from tool calls that represent missing/new target intent.
        /// </summary>
        private string ExtractCreationTargetFromToolArguments(string toolName, string argumentsJson) {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;

            try {
                using (JsonDocument document = JsonDocument.Parse(argumentsJson)) {
                    JsonElement root = document.RootElement;

                    if (toolName.Equals("create_file", StringComparison.Ordinal) &&
                        root.TryGetProperty("filePath", out JsonElement filePathElement) &&
                        filePathElement.ValueKind == JsonValueKind.String) {
                        string filePath = filePathElement.GetString();
                        return IsSafeCreateFileTarget(filePath) ? filePath : null;
                    }

                    if (toolName.Equals("file_search", StringComparison.Ordinal) &&
                        root.TryGetProperty("queries", out JsonElement queries) &&
                        queries.ValueKind == JsonValueKind.Array) {
                        foreach (JsonElement query in queries.EnumerateArray()) {
                            if (query.ValueKind == JsonValueKind.String &&
                                LooksLikeNewTargetFileName(query.GetString()))
                                return query.GetString();
                        }
                    }

                    if (toolName.Equals("file_search", StringComparison.Ordinal) &&
                        root.TryGetProperty("searchQueries", out JsonElement searchQueries) &&
                        searchQueries.ValueKind == JsonValueKind.Array) {
                        foreach (JsonElement query in searchQueries.EnumerateArray()) {
                            if (query.ValueKind == JsonValueKind.String &&
                                LooksLikeNewTargetFileName(query.GetString()))
                                return query.GetString();
                        }
                    }
                }
            } catch { }

            return null;
        }

        /// <summary>
        /// Counts repeated search/navigation calls related to a target path.
        /// </summary>
        private int CountRepeatedSearchOrNavigationAttempts(string requestBody, string targetPath) {
            int count = 0;
            string fileName = targetPath.Replace('\\', '/');
            int slash = fileName.LastIndexOf('/');
            if (slash >= 0)
                fileName = fileName.Substring(slash + 1);
            string stem = fileName;
            int dot = stem.IndexOf('.');
            if (dot > 0)
                stem = stem.Substring(0, dot);

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return 0;

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        if (!message.TryGetProperty("role", out JsonElement roleElement) ||
                            roleElement.ValueKind != JsonValueKind.String ||
                            !roleElement.GetString().Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                            !message.TryGetProperty("tool_calls", out JsonElement toolCalls) ||
                            toolCalls.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                                function.ValueKind != JsonValueKind.Object)
                                continue;

                            string name = ExtractStringProperty(function, "name") ?? "";
                            if (!IsSearchOrNavigationTool(name))
                                continue;

                            string arguments = function.TryGetProperty("arguments", out JsonElement argumentsElement)
                                ? argumentsElement.ToString()
                                : "";
                            string lower = arguments.ToLowerInvariant();
                            if (lower.Contains(fileName.ToLowerInvariant()) ||
                                lower.Contains(stem.ToLowerInvariant()))
                                count++;
                        }
                    }
                }
            } catch { }

            return count;
        }

        /// <summary>
        /// Returns true when a tool is a search/navigation/read probe rather than an edit.
        /// </summary>
        private bool IsSearchOrNavigationTool(string toolName) {
            return toolName.Equals("file_search", StringComparison.Ordinal) ||
                   toolName.Equals("code_search", StringComparison.Ordinal) ||
                   toolName.Equals("get_projects_in_solution", StringComparison.Ordinal) ||
                   toolName.Equals("get_files_in_project", StringComparison.Ordinal) ||
                   toolName.Equals("get_file", StringComparison.Ordinal) ||
                   toolName.Equals("find_symbol", StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true for likely filenames.
        /// </summary>
        private bool LooksLikeFileName(string text) {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string lower = text.ToLowerInvariant();
            return lower.EndsWith(".xaml") ||
                   lower.EndsWith(".xaml.vb") ||
                   lower.EndsWith(".vb") ||
                   lower.EndsWith(".cs") ||
                   lower.EndsWith(".proj") ||
                   lower.Contains(".");
        }

        /// <summary>
        /// Returns true for likely new target files, excluding common existing app/context files.
        /// </summary>
        private bool LooksLikeNewTargetFileName(string text) {
            if (!LooksLikeFileName(text))
                return false;

            string lower = text.ToLowerInvariant();
            if (lower.Contains("application.xaml") ||
                lower.Contains("mainwindow") ||
                lower.Contains("injectortimes") ||
                lower.Contains("maf.xaml") ||
                !IsSafeCreateFileTarget(text))
                return false;

            return true;
        }

        /// <summary>
        /// Returns true only for files the proxy may synthesize during a create-file recovery.
        /// </summary>
        private bool IsSafeCreateFileTarget(string filePath) {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string repaired = RepairPathControlCharacters(filePath).Trim().Trim('"', '\'', ',', ' ');
            string lower = repaired.Replace('\\', '/').ToLowerInvariant();

            if (lower.EndsWith(".sln", StringComparison.Ordinal) ||
                lower.EndsWith(".proj", StringComparison.Ordinal) ||
                lower.EndsWith(".vbproj", StringComparison.Ordinal) ||
                lower.EndsWith(".csproj", StringComparison.Ordinal) ||
                lower.EndsWith(".fsproj", StringComparison.Ordinal) ||
                lower.EndsWith(".props", StringComparison.Ordinal) ||
                lower.EndsWith(".targets", StringComparison.Ordinal))
                return false;

            if (lower.EndsWith("/app.xaml", StringComparison.Ordinal) ||
                lower.EndsWith("/application.xaml", StringComparison.Ordinal) ||
                lower.EndsWith("/application.xaml.vb", StringComparison.Ordinal) ||
                lower.EndsWith("/maf.xaml", StringComparison.Ordinal) ||
                lower.EndsWith("/maf.xaml.vb", StringComparison.Ordinal))
                return false;

            return lower.EndsWith(".xaml", StringComparison.Ordinal) ||
                   lower.EndsWith(".xaml.vb", StringComparison.Ordinal) ||
                   lower.EndsWith(".vb", StringComparison.Ordinal) ||
                   lower.EndsWith(".cs", StringComparison.Ordinal);
        }

        /// <summary>
        /// Rewrites unsafe create_file calls into harmless reads so project files are never clobbered.
        /// </summary>
        private void GuardUnsafeCreateFileTargets(List<ToolCallData> toolCalls, string requestBody) {
            if (toolCalls == null || toolCalls.Count == 0)
                return;

            bool canReadFile = IsToolAdvertised(requestBody, "get_file");
            foreach (ToolCallData toolCall in toolCalls) {
                if (toolCall == null ||
                    !toolCall.Name.Equals("create_file", StringComparison.Ordinal))
                    continue;

                string filePath = ExtractFilePathFromCreateFileArguments(toolCall.ArgumentsJson);
                if (IsSafeCreateFileTarget(filePath))
                    continue;

                if (!canReadFile) {
                    LogMessage("[Create File Guard] Unsafe create_file target has no safe read fallback: " + (filePath ?? "<missing>"));
                    continue;
                }

                string fallbackPath = string.IsNullOrWhiteSpace(filePath)
                    ? "Maf Scale\\ToolSelection.xaml"
                    : RepairPathControlCharacters(filePath).Trim().Trim('"', '\'', ',', ' ');

                toolCall.Name = "get_file";
                toolCall.ArgumentsJson = "{\"filename\":\"" + EscapeJson(fallbackPath) + "\",\"startLine\":1,\"endLine\":80,\"includeLineNumbers\":true}";
                LogMessage("[Create File Guard] Rewrote unsafe create_file target to get_file: " + fallbackPath);
            }
        }

        /// <summary>
        /// Returns true when the target file has already been read successfully in conversation history.
        /// </summary>
        private bool HasSuccessfulFileRead(string requestBody, string targetPath) {
            string normalizedTarget = NormalizePathForCompare(targetPath);

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return false;

                    var getFileCalls = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonElement message in messages.EnumerateArray()) {
                        string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                      roleElement.ValueKind == JsonValueKind.String
                            ? roleElement.GetString()
                            : "";

                        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                            message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array) {
                            foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                                if (!toolCall.TryGetProperty("id", out JsonElement idElement) ||
                                    idElement.ValueKind != JsonValueKind.String ||
                                    !toolCall.TryGetProperty("function", out JsonElement function) ||
                                    function.ValueKind != JsonValueKind.Object)
                                    continue;

                                string name = ExtractStringProperty(function, "name") ?? "";
                                if (!name.Equals("get_file", StringComparison.Ordinal))
                                    continue;

                                string arguments = function.TryGetProperty("arguments", out JsonElement argumentsElement)
                                    ? argumentsElement.ToString()
                                    : "";
                                string filename = ExtractFilenameFromGetFileArguments(arguments);
                                if (!string.IsNullOrWhiteSpace(filename))
                                    getFileCalls[idElement.GetString()] = filename;
                            }
                        } else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)) {
                            string toolCallId = message.TryGetProperty("tool_call_id", out JsonElement toolCallIdElement) &&
                                                toolCallIdElement.ValueKind == JsonValueKind.String
                                ? toolCallIdElement.GetString()
                                : "";
                            if (string.IsNullOrWhiteSpace(toolCallId) ||
                                !getFileCalls.TryGetValue(toolCallId, out string filename) ||
                                !NormalizePathForCompare(filename).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                                continue;

                            string content = message.TryGetProperty("content", out JsonElement contentElement)
                                ? contentElement.ToString()
                                : "";
                            if (!LooksLikeToolError(content) && !LooksLikeFileSearchMiss(content))
                                return true;
                        }
                    }
                }
            } catch { }

            return false;
        }

        /// <summary>
        /// Counts create_file attempts for a target path.
        /// </summary>
        private bool HasRepeatedCreateFileAttempts(string requestBody, string targetPath, int threshold) {
            int count = 0;
            string normalizedTarget = NormalizePathForCompare(targetPath);

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return false;

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        if (!message.TryGetProperty("role", out JsonElement roleElement) ||
                            roleElement.ValueKind != JsonValueKind.String ||
                            !roleElement.GetString().Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                            !message.TryGetProperty("tool_calls", out JsonElement toolCalls) ||
                            toolCalls.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                                function.ValueKind != JsonValueKind.Object)
                                continue;

                            string name = ExtractStringProperty(function, "name") ?? "";
                            if (!name.Equals("create_file", StringComparison.Ordinal))
                                continue;

                            string arguments = function.TryGetProperty("arguments", out JsonElement argumentsElement)
                                ? argumentsElement.ToString()
                                : "";
                            string filePath = ExtractFilePathFromCreateFileArguments(arguments);
                            if (!string.IsNullOrWhiteSpace(filePath) &&
                                NormalizePathForCompare(filePath).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)) {
                                count++;
                                if (count >= threshold)
                                    return true;
                            }
                        }
                    }
                }
            } catch { }

            return false;
        }

        /// <summary>
        /// Extracts filename from get_file arguments.
        /// </summary>
        private string ExtractFilenameFromGetFileArguments(string argumentsJson) {
            try {
                using (JsonDocument document = JsonDocument.Parse(argumentsJson)) {
                    return document.RootElement.TryGetProperty("filename", out JsonElement filenameElement) &&
                           filenameElement.ValueKind == JsonValueKind.String
                        ? filenameElement.GetString()
                        : null;
                }
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Extracts filePath from create_file arguments.
        /// </summary>
        private string ExtractFilePathFromCreateFileArguments(string argumentsJson) {
            try {
                using (JsonDocument document = JsonDocument.Parse(argumentsJson)) {
                    return document.RootElement.TryGetProperty("filePath", out JsonElement filePathElement) &&
                           filePathElement.ValueKind == JsonValueKind.String
                        ? filePathElement.GetString()
                        : null;
                }
            } catch {
                return TryExtractLikelyFilePath(argumentsJson);
            }
        }

        /// <summary>
        /// Normalizes paths for loose conversation-history comparisons.
        /// </summary>
        private string NormalizePathForCompare(string path) {
            return RepairPathControlCharacters(path ?? "")
                .Replace('/', '\\')
                .Trim()
                .Trim('"', '\'', ',', ' ')
                .TrimEnd('\\')
                .ToLowerInvariant();
        }

        /// <summary>
        /// Infers the primary project folder from file paths already used in the conversation.
        /// </summary>
        private string ExtractProjectFolderFromConversation(string requestBody) {
            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return null;

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        if (!message.TryGetProperty("role", out JsonElement roleElement) ||
                            roleElement.ValueKind != JsonValueKind.String ||
                            !roleElement.GetString().Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                            !message.TryGetProperty("tool_calls", out JsonElement toolCalls) ||
                            toolCalls.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                                function.ValueKind != JsonValueKind.Object)
                                continue;

                            string arguments = function.TryGetProperty("arguments", out JsonElement argumentsElement)
                                ? argumentsElement.ToString()
                                : "";

                            var match = System.Text.RegularExpressions.Regex.Match(arguments, @"(?<folder>[^""\\]+)\\[^""]+\.(xaml|vb|cs|vbproj|csproj)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (match.Success)
                                return match.Groups["folder"].Value;
                        }
                    }
                }
            } catch { }

            return null;
        }

        /// <summary>
        /// Gets the first successful tool result for a named tool.
        /// </summary>
        private string ExtractFirstToolResultForTool(string requestBody, string toolName) {
            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return null;

                    var toolCallIds = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonElement message in messages.EnumerateArray()) {
                        string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                      roleElement.ValueKind == JsonValueKind.String
                            ? roleElement.GetString()
                            : "";

                        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                            message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array) {
                            foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                                string id = toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                            idElement.ValueKind == JsonValueKind.String
                                    ? idElement.GetString()
                                    : "";
                                string name = "";
                                if (toolCall.TryGetProperty("function", out JsonElement function) &&
                                    function.ValueKind == JsonValueKind.Object &&
                                    function.TryGetProperty("name", out JsonElement nameElement) &&
                                    nameElement.ValueKind == JsonValueKind.String) {
                                    name = nameElement.GetString();
                                }

                                if (!string.IsNullOrEmpty(id) && name.Equals(toolName, StringComparison.Ordinal))
                                    toolCallIds[id] = name;
                            }
                        } else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)) {
                            string toolCallId = message.TryGetProperty("tool_call_id", out JsonElement toolCallIdElement) &&
                                                toolCallIdElement.ValueKind == JsonValueKind.String
                                ? toolCallIdElement.GetString()
                                : "";

                            if (!string.IsNullOrEmpty(toolCallId) && toolCallIds.ContainsKey(toolCallId)) {
                                string content = message.TryGetProperty("content", out JsonElement contentElement)
                                    ? contentElement.ToString()
                                    : "";
                                if (!string.IsNullOrWhiteSpace(content))
                                    return content.Trim();
                            }
                        }
                    }
                }
            } catch { }

            return null;
        }

        /// <summary>
        /// Counts assistant tool calls already present in the conversation history.
        /// </summary>
        private int CountHistoricalToolCalls(string requestBody, string toolName) {
            int count = 0;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return 0;

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                      roleElement.ValueKind == JsonValueKind.String
                            ? roleElement.GetString()
                            : "";

                        if (!role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                            !message.TryGetProperty("tool_calls", out JsonElement toolCalls) ||
                            toolCalls.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                                function.ValueKind != JsonValueKind.Object)
                                continue;

                            string name = ExtractStringProperty(function, "name") ?? "";
                            if (name.Equals(toolName, StringComparison.Ordinal))
                                count++;
                        }
                    }
                }
            } catch { }

            return count;
        }

        /// <summary>
        /// Extracts the first project file path from a get_projects_in_solution result.
        /// </summary>
        private string ExtractProjectPathFromToolResult(string content) {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            string[] lines = content.Replace("\r", "").Split('\n');
            foreach (string rawLine in lines) {
                string line = rawLine.Trim().Trim('`');
                if (line.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                    line.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    line.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)) {
                    return RepairPathControlCharacters(line);
                }
            }

            return null;
        }

        /// <summary>
        /// Removes Qwen tool-call blocks from generated text.
        /// </summary>
        private string StripQwenToolCalls(string content, string requestBody) {
            if (string.IsNullOrEmpty(content))
                return "";

            var sb = new StringBuilder();
            int searchStart = 0;

            while (searchStart < content.Length) {
                int start = content.IndexOf("<tool_call>", searchStart, StringComparison.OrdinalIgnoreCase);
                if (start < 0) {
                    sb.Append(content.Substring(searchStart));
                    break;
                }

                sb.Append(content.Substring(searchStart, start - searchStart));
                int end = content.IndexOf("</tool_call>", start, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                    break;

                searchStart = end + "</tool_call>".Length;
            }

            string stripped = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(stripped)) {
                string userRequest = ExtractLastUserMessage(requestBody);
                stripped = string.IsNullOrWhiteSpace(userRequest)
                    ? "Continue from the current Visual Studio context. Do not read the project again."
                    : "Continue from the current Visual Studio context without reading the project again: " + userRequest;
            }

            return stripped;
        }

        /// <summary>
        /// Removes visible reasoning tags and collapses repeated generated text.
        /// </summary>
        private string SanitizeAssistantText(string content) {
            if (string.IsNullOrWhiteSpace(content))
                return content;

            string withoutThinking = RemoveTaggedBlocks(content, "thinking").Trim();
            if (string.IsNullOrWhiteSpace(withoutThinking))
                withoutThinking = content;

            return CollapseRepeatedText(withoutThinking).Trim();
        }

        /// <summary>
        /// Removes simple XML-like tagged blocks from assistant text.
        /// </summary>
        private string RemoveTaggedBlocks(string content, string tagName) {
            string openTag = "<" + tagName + ">";
            string closeTag = "</" + tagName + ">";
            var sb = new StringBuilder();
            int searchStart = 0;

            while (searchStart < content.Length) {
                int start = content.IndexOf(openTag, searchStart, StringComparison.OrdinalIgnoreCase);
                if (start < 0) {
                    sb.Append(content.Substring(searchStart));
                    break;
                }

                sb.Append(content.Substring(searchStart, start - searchStart));
                int end = content.IndexOf(closeTag, start + openTag.Length, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                    break;

                searchStart = end + closeTag.Length;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Collapses repeated paragraphs and repeated sentence-like lines.
        /// </summary>
        private string CollapseRepeatedText(string content) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var output = new List<string>();

            string normalizedNewlines = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] chunks = normalizedNewlines.Split(new[] { '\n' }, StringSplitOptions.None);

            foreach (string chunk in chunks) {
                string trimmed = chunk.Trim();
                if (string.IsNullOrEmpty(trimmed)) {
                    if (output.Count > 0 && output[output.Count - 1].Length != 0)
                        output.Add("");
                    continue;
                }

                string key = NormalizeRepeatKey(trimmed);
                if (seen.Contains(key))
                    continue;

                seen.Add(key);
                output.Add(trimmed);
            }

            string collapsed = string.Join("\n", output).Trim();
            string repeatedSentence = "The build should be successful as no new files were added and existing files weren't modified.";
            int first = collapsed.IndexOf(repeatedSentence, StringComparison.Ordinal);
            if (first >= 0) {
                int second = collapsed.IndexOf(repeatedSentence, first + repeatedSentence.Length, StringComparison.Ordinal);
                if (second >= 0)
                    collapsed = collapsed.Substring(0, first + repeatedSentence.Length).Trim();
            }

            const int maxChars = 4000;
            if (collapsed.Length > maxChars)
                collapsed = collapsed.Substring(0, maxChars).Trim() + "\n\n[Response truncated by proxy after repeated output.]";

            return collapsed;
        }

        /// <summary>
        /// Normalizes text used for repeated-output detection.
        /// </summary>
        private string NormalizeRepeatKey(string text) {
            return text.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Extracts the last user message from the request body.
        /// </summary>
        private string ExtractLastUserMessage(string requestBody) {
            string lastUserMessage = null;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return null;

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        if (message.TryGetProperty("role", out JsonElement role) &&
                            role.ValueKind == JsonValueKind.String &&
                            role.GetString().Equals("user", StringComparison.OrdinalIgnoreCase) &&
                            message.TryGetProperty("content", out JsonElement content) &&
                            content.ValueKind == JsonValueKind.String) {
                            lastUserMessage = content.GetString();
                        }
                    }
                }
            } catch { }

            return lastUserMessage;
        }

        /// <summary>
        /// Builds a simple OpenAI-compatible streaming text response.
        /// </summary>
        private string BuildTextOnlySseResponse(string content, string model) {
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string completionId = "chatcmpl_" + Guid.NewGuid().ToString("N");
            string safeContent = EscapeJson(content ?? "");

            var sb = new StringBuilder();
            sb.Append("data: {\"id\":\"").Append(completionId)
              .Append("\",\"object\":\"chat.completion.chunk\",\"created\":").Append(created)
              .Append(",\"model\":\"").Append(EscapeJson(model ?? "lm-studio"))
              .Append("\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n");

            sb.Append("data: {\"id\":\"").Append(completionId)
              .Append("\",\"object\":\"chat.completion.chunk\",\"created\":").Append(created)
              .Append(",\"model\":\"").Append(EscapeJson(model ?? "lm-studio"))
              .Append("\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"")
              .Append(safeContent)
              .Append("\"},\"finish_reason\":null}]}\n\n");

            sb.Append("data: {\"id\":\"").Append(completionId)
              .Append("\",\"object\":\"chat.completion.chunk\",\"created\":").Append(created)
              .Append(",\"model\":\"").Append(EscapeJson(model ?? "lm-studio"))
              .Append("\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            sb.Append("data: [DONE]\n\n");

            return sb.ToString();
        }

        /// <summary>
        /// Parses one Qwen tool-call JSON object.
        /// </summary>
        private ToolCallData ParseQwenToolCall(string json) {
            try {
                using (JsonDocument document = JsonDocument.Parse(json)) {
                    string name = document.RootElement.TryGetProperty("name", out JsonElement nameElement) &&
                                  nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : "";

                    string arguments = "{}";
                    if (document.RootElement.TryGetProperty("arguments", out JsonElement argumentsElement)) {
                        arguments = argumentsElement.ValueKind == JsonValueKind.String
                            ? argumentsElement.GetString()
                            : argumentsElement.GetRawText();
                    }

                    if (string.IsNullOrWhiteSpace(name))
                        return null;

                    string repairedArguments = RepairKnownToolArguments(name, arguments);
                    if (!string.Equals(arguments ?? "{}", repairedArguments ?? "{}", StringComparison.Ordinal)) {
                        LogMessage("[Tool Args Repair] " + name + ": " +
                                   TruncateForLog(arguments, 300) + " -> " +
                                   TruncateForLog(repairedArguments, 300));
                    }

                    return new ToolCallData {
                        Name = name,
                        ArgumentsJson = repairedArguments
                    };
                }
            } catch (Exception ex) {
                LogMessage($"Error parsing Qwen tool call: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Repairs common malformed Qwen tool arguments before Visual Studio receives them.
        /// </summary>
        private string RepairKnownToolArguments(string toolName, string argumentsJson) {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                argumentsJson = "{}";

            try {
                using (JsonDocument document = JsonDocument.Parse(argumentsJson)) {
                    if (toolName.Equals("code_search", StringComparison.Ordinal) &&
                        (!document.RootElement.TryGetProperty("searchQueries", out JsonElement searchQueries) ||
                         searchQueries.ValueKind != JsonValueKind.Array ||
                         searchQueries.GetArrayLength() == 0)) {
                        return "{\"searchQueries\":[\"fkkvs\",\"ToolSelection.xaml\",\"tool selection window\",\"new tool window\"]}";
                    }

                    if (toolName.Equals("file_search", StringComparison.Ordinal)) {
                        return RepairFileSearchArguments(document.RootElement);
                    }

                    if (toolName.Equals("get_files_in_project", StringComparison.Ordinal) &&
                        (!document.RootElement.TryGetProperty("projectPath", out JsonElement projectPath) ||
                         projectPath.ValueKind != JsonValueKind.String ||
                         string.IsNullOrWhiteSpace(projectPath.GetString()))) {
                        return "{\"projectPath\":\"Maf Scale\\\\ME7Tools.vbproj\"}";
                    }

                    if (toolName.Equals("get_file", StringComparison.Ordinal)) {
                        string filename = document.RootElement.TryGetProperty("filename", out JsonElement filenameElement) &&
                                          filenameElement.ValueKind == JsonValueKind.String
                            ? filenameElement.GetString()
                            : "";

                        int startLine = document.RootElement.TryGetProperty("startLine", out JsonElement startLineElement) &&
                                        startLineElement.ValueKind == JsonValueKind.Number &&
                                        startLineElement.TryGetInt32(out int parsedStartLine)
                            ? parsedStartLine
                            : 1;

                        int endLine = document.RootElement.TryGetProperty("endLine", out JsonElement endLineElement) &&
                                      endLineElement.ValueKind == JsonValueKind.Number &&
                                      endLineElement.TryGetInt32(out int parsedEndLine)
                            ? parsedEndLine
                            : 240;

                        filename = RepairPathControlCharacters(filename);

                        if (string.IsNullOrWhiteSpace(filename))
                            filename = "Maf Scale\\\\ToolSelection.xaml";

                        if (startLine <= 0)
                            startLine = 1;
                        if (endLine < startLine)
                            endLine = startLine + 239;

                        return "{\"filename\":\"" + EscapeJson(filename) + "\",\"startLine\":" +
                               startLine.ToString() + ",\"endLine\":" + endLine.ToString() +
                               ",\"includeLineNumbers\":true}";
                    }

                    if (toolName.Equals("run_command_in_terminal", StringComparison.Ordinal)) {
                        return RepairRunCommandInTerminalArguments(document.RootElement);
                    }

                    if (toolName.Equals("replace_string_in_file", StringComparison.Ordinal)) {
                        return RepairReplaceStringArguments(document.RootElement);
                    }

                    if (toolName.Equals("create_file", StringComparison.Ordinal)) {
                        return RepairCreateFileArguments(document.RootElement, argumentsJson);
                    }

                    if (toolName.Equals("multi_replace_string_in_file", StringComparison.Ordinal)) {
                        return RepairMultiReplaceArguments(document.RootElement);
                    }

                    if (toolName.Equals("update_plan_progress", StringComparison.Ordinal)) {
                        return RepairUpdatePlanProgressArguments(document.RootElement);
                    }
                }
            } catch (Exception ex) {
                LogMessage("[Tool Args Repair] Could not parse " + toolName + " arguments: " + ex.Message +
                           "; raw=" + TruncateForLog(argumentsJson, 300));
                if (toolName.Equals("code_search", StringComparison.Ordinal))
                    return "{\"searchQueries\":[\"fkkvs\",\"ToolSelection.xaml\",\"tool selection window\",\"new tool window\"]}";
                if (toolName.Equals("file_search", StringComparison.Ordinal))
                    return "{\"queries\":[\"ToolSelection.xaml\",\"fkkvs\"],\"maxResults\":20}";
                if (toolName.Equals("get_files_in_project", StringComparison.Ordinal))
                    return "{\"projectPath\":\"Maf Scale\\\\ME7Tools.vbproj\"}";
                if (toolName.Equals("get_file", StringComparison.Ordinal))
                    return "{\"filename\":\"Maf Scale\\\\ToolSelection.xaml\",\"startLine\":1,\"endLine\":240,\"includeLineNumbers\":true}";
                if (toolName.Equals("run_command_in_terminal", StringComparison.Ordinal))
                    return "{\"command\":\"Write-Host \\\"Command arguments were malformed and were blocked by LmVsProxy.\\\"\",\"summary\":\"Malformed command blocked by proxy\",\"background\":false}";
                if (toolName.Equals("replace_string_in_file", StringComparison.Ordinal))
                    return "{\"filePath\":\"Maf Scale\\\\ToolSelection.xaml\",\"oldString\":\"\",\"newString\":\"\"}";
                if (toolName.Equals("create_file", StringComparison.Ordinal))
                    return BuildCreateFileArguments(TryExtractLikelyFilePath(argumentsJson), null);
                if (toolName.Equals("multi_replace_string_in_file", StringComparison.Ordinal))
                    return "{\"replacements\":[],\"explanation\":\"Malformed multi_replace_string_in_file arguments were repaired by LmVsProxy.\"}";
                if (toolName.Equals("update_plan_progress", StringComparison.Ordinal))
                    return "{\"stepId\":\"step-1\",\"status\":\"completed\",\"message\":\"Progress updated by LmVsProxy after malformed update_plan_progress arguments.\",\"autoAdvance\":true}";
            }

            return string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        }

        /// <summary>
        /// Ensures update_plan_progress has all required fields so VS does not bounce the model into an argument repair loop.
        /// </summary>
        private string RepairUpdatePlanProgressArguments(JsonElement arguments) {
            string stepId = arguments.TryGetProperty("stepId", out JsonElement stepIdElement) &&
                            stepIdElement.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(stepIdElement.GetString())
                ? stepIdElement.GetString()
                : "step-1";

            string status = arguments.TryGetProperty("status", out JsonElement statusElement) &&
                            statusElement.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(statusElement.GetString())
                ? statusElement.GetString()
                : "completed";

            status = NormalizePlanProgressStatus(status);

            string message = arguments.TryGetProperty("message", out JsonElement messageElement) &&
                             messageElement.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrWhiteSpace(messageElement.GetString())
                ? messageElement.GetString()
                : "Progress updated by LmVsProxy.";

            bool autoAdvance = true;
            if (arguments.TryGetProperty("autoAdvance", out JsonElement autoAdvanceElement)) {
                if (autoAdvanceElement.ValueKind == JsonValueKind.False)
                    autoAdvance = false;
                else if (autoAdvanceElement.ValueKind == JsonValueKind.String &&
                         bool.TryParse(autoAdvanceElement.GetString(), out bool parsedAutoAdvance))
                    autoAdvance = parsedAutoAdvance;
            }

            return "{\"stepId\":\"" + EscapeJson(stepId) +
                   "\",\"status\":\"" + EscapeJson(status) +
                   "\",\"message\":\"" + EscapeJson(message) +
                   "\",\"autoAdvance\":" + (autoAdvance ? "true" : "false") + "}";
        }

        private string NormalizePlanProgressStatus(string status) {
            string normalized = (status ?? "").Trim().Replace("_", "-").ToLowerInvariant();
            switch (normalized) {
                case "pending":
                case "in-progress":
                case "completed":
                case "failed":
                case "skipped":
                    return normalized;
                case "started":
                case "running":
                    return "in-progress";
                case "complete":
                case "done":
                case "success":
                    return "completed";
                default:
                    return "completed";
            }
        }

        /// <summary>
        /// Ensures create_file has both required fields: filePath and content.
        /// </summary>
        private string RepairCreateFileArguments(JsonElement arguments, string rawArgumentsJson) {
            string filePath = arguments.TryGetProperty("filePath", out JsonElement filePathElement) &&
                              filePathElement.ValueKind == JsonValueKind.String
                ? filePathElement.GetString()
                : "";

            if (string.IsNullOrWhiteSpace(filePath))
                filePath = TryExtractLikelyFilePath(rawArgumentsJson);

            string content = arguments.TryGetProperty("content", out JsonElement contentElement) &&
                             contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;

            content = NormalizeGeneratedFileText(content);
            return BuildCreateFileArguments(filePath, content);
        }

        /// <summary>
        /// Builds safe create_file arguments, supplying a minimal file body if the model omitted one.
        /// </summary>
        private string BuildCreateFileArguments(string filePath, string content) {
            filePath = RepairPathControlCharacters(filePath);
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = "Maf Scale\\fkkvs.xaml.vb";

            filePath = filePath.Trim().Trim('"', '\'', ',', ' ');
            filePath = TrimDanglingSlashAfterFileExtension(filePath);

            if (filePath.EndsWith("\\", StringComparison.Ordinal) ||
                filePath.EndsWith("/", StringComparison.Ordinal))
                filePath += "fkkvs.xaml.vb";

            if (!IsSafeCreateFileTarget(filePath)) {
                LogMessage("[Tool Args Repair] create_file target is protected and will be guarded: " + filePath);
                return "{\"filePath\":\"" + EscapeJson(filePath) + "\",\"content\":\"" + EscapeJson(NormalizeGeneratedFileText(content) ?? "") + "\"}";
            }

            content = NormalizeGeneratedFileText(content);
            if (string.IsNullOrWhiteSpace(content)) {
                content = BuildMinimalFileContent(filePath);
                LogMessage("[Tool Args Repair] create_file was missing content; generated minimal content for " + filePath);
            }

            return "{\"filePath\":\"" + EscapeJson(filePath) + "\",\"content\":\"" + EscapeJson(content) + "\"}";
        }

        /// <summary>
        /// Normalizes replace_string_in_file text fields so escaped newlines become real newlines.
        /// </summary>
        private string RepairReplaceStringArguments(JsonElement arguments) {
            string filePath = arguments.TryGetProperty("filePath", out JsonElement filePathElement) &&
                              filePathElement.ValueKind == JsonValueKind.String
                ? RepairPathControlCharacters(filePathElement.GetString())
                : "";

            string oldString = arguments.TryGetProperty("oldString", out JsonElement oldStringElement) &&
                               oldStringElement.ValueKind == JsonValueKind.String
                ? oldStringElement.GetString()
                : "";

            string newString = arguments.TryGetProperty("newString", out JsonElement newStringElement) &&
                               newStringElement.ValueKind == JsonValueKind.String
                ? newStringElement.GetString()
                : "";

            oldString = NormalizeGeneratedFileText(oldString);
            newString = NormalizeGeneratedFileText(newString);

            return "{\"filePath\":\"" + EscapeJson(filePath) +
                   "\",\"oldString\":\"" + EscapeJson(oldString ?? "") +
                   "\",\"newString\":\"" + EscapeJson(newString ?? "") + "\"}";
        }

        /// <summary>
        /// Converts literal escape text such as \n into real line breaks and collapses doubled output.
        /// </summary>
        private string NormalizeGeneratedFileText(string text) {
            if (text == null)
                return null;

            string normalized = text
                .Replace("\\r\\n", "\n")
                .Replace("\\n", "\n")
                .Replace("\\r", "\n")
                .Replace("\\t", "\t");

            normalized = NormalizeLineEndings(normalized);
            normalized = CollapseDuplicatedGeneratedContent(normalized);

            if (!string.Equals(text, normalized, StringComparison.Ordinal))
                LogMessage("[Tool Args Repair] Normalized generated file text escapes/duplicates.");

            return normalized;
        }

        /// <summary>
        /// Normalizes all line endings to the platform line ending.
        /// </summary>
        private string NormalizeLineEndings(string text) {
            if (text == null)
                return null;

            string lf = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return lf.Replace("\n", Environment.NewLine);
        }

        /// <summary>
        /// Removes accidental exact duplicate generated content blocks.
        /// </summary>
        private string CollapseDuplicatedGeneratedContent(string text) {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            string lf = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string trimmed = lf.Trim();
            int middle = trimmed.Length / 2;
            int start = Math.Max(1, middle - 20);
            int end = Math.Min(trimmed.Length - 1, middle + 20);

            for (int i = start; i <= end; i++) {
                if (trimmed[i - 1] != '\n' && trimmed[i] != '\n')
                    continue;

                string first = trimmed.Substring(0, i).Trim();
                string second = trimmed.Substring(i).Trim();
                if (first.Length > 0 && first.Equals(second, StringComparison.Ordinal))
                    return NormalizeLineEndings(first) + Environment.NewLine;
            }

            return text;
        }

        /// <summary>
        /// Removes a trailing slash caused by malformed quoted paths such as "file.vb\", ".
        /// </summary>
        private string TrimDanglingSlashAfterFileExtension(string filePath) {
            if (string.IsNullOrWhiteSpace(filePath) ||
                (!filePath.EndsWith("\\", StringComparison.Ordinal) &&
                 !filePath.EndsWith("/", StringComparison.Ordinal)))
                return filePath;

            string withoutSlash = filePath.Substring(0, filePath.Length - 1);
            string extension = Path.GetExtension(withoutSlash);
            if (string.IsNullOrWhiteSpace(extension))
                return filePath;

            return withoutSlash;
        }

        /// <summary>
        /// Generates a conservative file body for create_file when the model provides only a path.
        /// </summary>
        private string BuildMinimalFileContent(string filePath) {
            string normalized = filePath ?? "";
            string fileName = normalized.Replace('\\', '/');
            int slash = fileName.LastIndexOf('/');
            if (slash >= 0)
                fileName = fileName.Substring(slash + 1);

            string className = fileName;
            int dot = className.IndexOf('.');
            if (dot > 0)
                className = className.Substring(0, dot);
            if (string.IsNullOrWhiteSpace(className))
                className = "fkkvs";

            if (normalized.EndsWith(".xaml.vb", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)) {
                return "Public Class " + className + Environment.NewLine +
                       "End Class" + Environment.NewLine;
            }

            if (normalized.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
                return "<Window x:Class=\"" + className + "\"" + Environment.NewLine +
                       "        xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" + Environment.NewLine +
                       "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"" + Environment.NewLine +
                       "        Title=\"" + className + "\" Height=\"400\" Width=\"800\">" + Environment.NewLine +
                       "    <Grid />" + Environment.NewLine +
                       "</Window>" + Environment.NewLine;
            }

            return "";
        }

        /// <summary>
        /// Pulls a likely file path out of malformed JSON-ish tool arguments.
        /// </summary>
        private string TryExtractLikelyFilePath(string rawArgumentsJson) {
            if (string.IsNullOrWhiteSpace(rawArgumentsJson))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(
                rawArgumentsJson,
                @"""filePath""\s*:\s*""(?<path>[^""]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            string path = match.Groups["path"].Value;
            path = path.Replace("\\\"", "\"");
            return path.Trim().Trim('"', '\'', ',', ' ');
        }

        /// <summary>
        /// Ensures multi_replace_string_in_file has the required explanation field.
        /// </summary>
        private string RepairMultiReplaceArguments(JsonElement arguments) {
            string explanation = arguments.TryGetProperty("explanation", out JsonElement explanationElement) &&
                                 explanationElement.ValueKind == JsonValueKind.String &&
                                 !string.IsNullOrWhiteSpace(explanationElement.GetString())
                ? explanationElement.GetString()
                : "Apply multiple replacements generated by LmVsProxy.";

            string replacementsJson = arguments.TryGetProperty("replacements", out JsonElement replacementsElement) &&
                                      replacementsElement.ValueKind == JsonValueKind.Array
                ? BuildNormalizedReplacementsJson(replacementsElement)
                : "[]";

            return "{\"replacements\":" + replacementsJson + ",\"explanation\":\"" + EscapeJson(explanation) + "\"}";
        }

        /// <summary>
        /// Normalizes text fields inside multi_replace_string_in_file replacements.
        /// </summary>
        private string BuildNormalizedReplacementsJson(JsonElement replacementsElement) {
            using (var stream = new MemoryStream())
            using (var writer = new Utf8JsonWriter(stream)) {
                writer.WriteStartArray();

                foreach (JsonElement replacement in replacementsElement.EnumerateArray()) {
                    if (replacement.ValueKind != JsonValueKind.Object) {
                        replacement.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartObject();
                    foreach (JsonProperty property in replacement.EnumerateObject()) {
                        if ((property.NameEquals("oldString") || property.NameEquals("newString")) &&
                            property.Value.ValueKind == JsonValueKind.String) {
                            writer.WriteString(property.Name, NormalizeGeneratedFileText(property.Value.GetString()) ?? "");
                        } else if (property.NameEquals("filePath") &&
                                   property.Value.ValueKind == JsonValueKind.String) {
                            writer.WriteString(property.Name, RepairPathControlCharacters(property.Value.GetString()) ?? "");
                        } else {
                            property.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// Normalizes file_search arguments to Visual Studio's required schema.
        /// </summary>
        private string RepairFileSearchArguments(JsonElement arguments) {
            var queries = new List<string>();
            AddStringArrayOrStringProperty(arguments, "queries", queries);
            AddStringArrayOrStringProperty(arguments, "query", queries);
            AddStringArrayOrStringProperty(arguments, "searchQuery", queries);
            AddStringArrayOrStringProperty(arguments, "searchQueries", queries);
            AddStringArrayOrStringProperty(arguments, "file", queries);
            AddStringArrayOrStringProperty(arguments, "filename", queries);
            AddStringArrayOrStringProperty(arguments, "fileName", queries);
            AddStringArrayOrStringProperty(arguments, "path", queries);

            if (queries.Count == 0) {
                queries.Add("ToolSelection.xaml");
                queries.Add("fkkvs");
            }

            int maxResults = 20;
            if (!TryGetIntProperty(arguments, "maxResults", out maxResults) &&
                !TryGetIntProperty(arguments, "max_results", out maxResults)) {
                maxResults = 20;
            }

            if (maxResults <= 0)
                maxResults = 20;
            if (maxResults > 50)
                maxResults = 50;

            var sb = new StringBuilder();
            sb.Append("{\"queries\":[");
            for (int i = 0; i < queries.Count; i++) {
                if (i > 0)
                    sb.Append(",");
                sb.Append("\"").Append(EscapeJson(queries[i])).Append("\"");
            }
            sb.Append("],\"maxResults\":").Append(maxResults.ToString()).Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Adds a string or array of strings from a JSON property to a query list.
        /// </summary>
        private void AddStringArrayOrStringProperty(JsonElement arguments, string propertyName, List<string> values) {
            if (!arguments.TryGetProperty(propertyName, out JsonElement property))
                return;

            if (property.ValueKind == JsonValueKind.String) {
                AddDistinctNonEmpty(values, property.GetString());
                return;
            }

            if (property.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement item in property.EnumerateArray()) {
                if (item.ValueKind == JsonValueKind.String)
                    AddDistinctNonEmpty(values, item.GetString());
            }
        }

        /// <summary>
        /// Adds a unique non-empty value to a list.
        /// </summary>
        private void AddDistinctNonEmpty(List<string> values, string value) {
            if (values == null || string.IsNullOrWhiteSpace(value))
                return;

            string trimmed = RepairPathControlCharacters(value.Trim());
            foreach (string existing in values) {
                if (existing.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            values.Add(trimmed);
        }

        /// <summary>
        /// Converts JSON escape characters that were accidentally decoded inside path-like strings back to path separators.
        /// </summary>
        private string RepairPathControlCharacters(string value) {
            if (string.IsNullOrEmpty(value))
                return value;

            StringBuilder sb = null;
            for (int i = 0; i < value.Length; i++) {
                char ch = value[i];
                string replacement = null;

                switch (ch) {
                    case '\f':
                        replacement = "\\f";
                        break;
                    case '\t':
                        replacement = "\\t";
                        break;
                    case '\n':
                        replacement = "\\n";
                        break;
                    case '\r':
                        replacement = "\\r";
                        break;
                    case '\b':
                        replacement = "\\b";
                        break;
                }

                if (replacement != null) {
                    if (sb == null) {
                        sb = new StringBuilder(value.Length + 4);
                        sb.Append(value, 0, i);
                    }

                    sb.Append(replacement);
                    continue;
                }

                if (sb != null)
                    sb.Append(ch);
            }

            if (sb == null)
                return value;

            string repaired = sb.ToString();
            LogMessage("[Path Repair] " + EscapeControlCharsForLog(value) + " -> " + EscapeControlCharsForLog(repaired));
            return repaired;
        }

        /// <summary>
        /// Reads an integer JSON property that may be encoded as a number or string.
        /// </summary>
        private bool TryGetIntProperty(JsonElement arguments, string propertyName, out int value) {
            value = 0;
            if (!arguments.TryGetProperty(propertyName, out JsonElement property))
                return false;

            if (property.ValueKind == JsonValueKind.Number)
                return property.TryGetInt32(out value);

            if (property.ValueKind == JsonValueKind.String)
                return int.TryParse(property.GetString(), out value);

            return false;
        }

        /// <summary>
        /// Repairs common malformed PowerShell command arguments generated by small tool-use models.
        /// </summary>
        private string RepairRunCommandInTerminalArguments(JsonElement arguments) {
            string command = arguments.TryGetProperty("command", out JsonElement commandElement) &&
                             commandElement.ValueKind == JsonValueKind.String
                ? commandElement.GetString()
                : "";

            string summary = arguments.TryGetProperty("summary", out JsonElement summaryElement) &&
                             summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString()
                : "Run command";

            bool background = arguments.TryGetProperty("background", out JsonElement backgroundElement) &&
                              backgroundElement.ValueKind == JsonValueKind.True;

            command = RepairPowerShellHereStrings(command);

            return "{\"command\":\"" + EscapeJson(command) +
                   "\",\"summary\":\"" + EscapeJson(summary) +
                   "\",\"background\":" + (background ? "true" : "false") + "}";
        }

        /// <summary>
        /// Ensures PowerShell here-string headers and terminators are on their own lines.
        /// </summary>
        private string RepairPowerShellHereStrings(string command) {
            if (string.IsNullOrEmpty(command))
                return command;

            string repaired = command
                .Replace("@'\\n", "@'\n")
                .Replace("@\"\\n", "@\"\n")
                .Replace("\\n'@", "\n'@")
                .Replace("\\n\"@", "\n\"@");

            repaired = System.Text.RegularExpressions.Regex.Replace(repaired, @"@'(?=\S)", "@'\n");
            repaired = System.Text.RegularExpressions.Regex.Replace(repaired, "@\"(?=\\S)", "@\"\n");
            repaired = System.Text.RegularExpressions.Regex.Replace(repaired, @"(?<!\n)'@", "\n'@");
            repaired = System.Text.RegularExpressions.Regex.Replace(repaired, "(?<!\\n)\"@", "\n\"@");

            return repaired;
        }

        /// <summary>
        /// Extracts a top-level string property from JSON.
        /// </summary>
        private string ExtractJsonStringProperty(string json, string propertyName) {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
                return null;

            try {
                using (JsonDocument document = JsonDocument.Parse(json)) {
                    return document.RootElement.TryGetProperty(propertyName, out JsonElement value) &&
                           value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                }
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Applies LM Studio compatibility fixes before forwarding chat completions.
        /// </summary>
        private string PrepareRequestBodyForLmStudio(HttpListenerRequest originalRequest, string requestBody) {
            if (originalRequest?.Url?.AbsolutePath == null || string.IsNullOrWhiteSpace(requestBody))
                return requestBody;

            if (!originalRequest.Url.AbsolutePath.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                return requestBody;

            requestBody = AddCreateFileHintAfterRepeatedFileSearchMisses(requestBody);
            requestBody = AddToolSchemaRecoveryHints(requestBody);
            requestBody = AddBuildErrorRepairHint(requestBody);
            return requestBody;
        }

        /// <summary>
        /// Adds persistence guidance after build/get_errors failures so the model edits instead of stopping.
        /// </summary>
        private string AddBuildErrorRepairHint(string requestBody) {
            try {
                if (!HasUnfixedBuildOrErrorFailure(requestBody, out string errorFile))
                    return requestBody;

                const string hintMarker = "[LmVsProxy build error repair hint]";
                if (requestBody.Contains(hintMarker))
                    return requestBody;

                string target = string.IsNullOrWhiteSpace(errorFile)
                    ? "the failing files"
                    : errorFile;

                string hint = hintMarker + " Visual Studio has returned build/get_errors failures and no edit tool has succeeded afterward. " +
                              "Do not end the response after describing the errors. Read the failing file if needed, then use replace_string_in_file or multi_replace_string_in_file to fix it. " +
                              "Do not run run_build again until after an edit. Current primary failing target: " + target + ".";

                string rewritten = AppendSystemMessage(requestBody, hint);
                if (!ReferenceEquals(rewritten, requestBody))
                    LogMessage("[Build Error Hint] Added repair guidance for " + target + ".");

                return rewritten;
            } catch (Exception ex) {
                LogMessage("[Build Error Hint] Error: " + ex.Message);
                return requestBody;
            }
        }

        /// <summary>
        /// Adds schema reminders when VS reports repeated missing required tool parameters.
        /// </summary>
        private string AddToolSchemaRecoveryHints(string requestBody) {
            try {
                string hint = BuildToolSchemaRecoveryHint(requestBody);
                if (string.IsNullOrWhiteSpace(hint))
                    return requestBody;

                string marker = "[LmVsProxy tool schema recovery hint]";
                if (requestBody.Contains(marker))
                    return requestBody;

                string rewritten = AppendSystemMessage(requestBody, marker + " " + hint);
                if (!ReferenceEquals(rewritten, requestBody))
                    LogMessage("[Tool Schema Hint] Added schema recovery guidance.");

                return rewritten;
            } catch (Exception ex) {
                LogMessage("[Tool Schema Hint] Error: " + ex.Message);
                return requestBody;
            }
        }

        /// <summary>
        /// Builds a concise schema reminder from tool-result errors in conversation history.
        /// </summary>
        private string BuildToolSchemaRecoveryHint(string requestBody) {
            bool createFileMissingContent = false;
            bool multiReplaceMissingExplanation = false;
            bool multiReplaceMissingReplacements = false;
            bool updatePlanMissingAutoAdvance = false;

            using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                    messages.ValueKind != JsonValueKind.Array)
                    return null;

                var toolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonElement message in messages.EnumerateArray()) {
                    string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                  roleElement.ValueKind == JsonValueKind.String
                        ? roleElement.GetString()
                        : "";

                    if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                        message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                        toolCalls.ValueKind == JsonValueKind.Array) {
                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            string id = toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                        idElement.ValueKind == JsonValueKind.String
                                ? idElement.GetString()
                                : "";
                            string name = "";
                            if (toolCall.TryGetProperty("function", out JsonElement function) &&
                                function.ValueKind == JsonValueKind.Object &&
                                function.TryGetProperty("name", out JsonElement nameElement) &&
                                nameElement.ValueKind == JsonValueKind.String) {
                                name = nameElement.GetString();
                            }

                            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                                toolCallNames[id] = name;
                        }
                    } else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)) {
                        string toolCallId = message.TryGetProperty("tool_call_id", out JsonElement toolCallIdElement) &&
                                            toolCallIdElement.ValueKind == JsonValueKind.String
                            ? toolCallIdElement.GetString()
                            : "";
                        string toolName = !string.IsNullOrWhiteSpace(toolCallId) &&
                                          toolCallNames.TryGetValue(toolCallId, out string mappedName)
                            ? mappedName
                            : "";
                        string content = message.TryGetProperty("content", out JsonElement contentElement)
                            ? contentElement.ToString()
                            : "";
                        string lower = content.ToLowerInvariant();

                        if (toolName.Equals("create_file", StringComparison.Ordinal) &&
                            lower.Contains("missing parameter content"))
                            createFileMissingContent = true;
                        if (toolName.Equals("multi_replace_string_in_file", StringComparison.Ordinal) &&
                            lower.Contains("missing parameter explanation"))
                            multiReplaceMissingExplanation = true;
                        if (toolName.Equals("multi_replace_string_in_file", StringComparison.Ordinal) &&
                            lower.Contains("missing parameter replacements"))
                            multiReplaceMissingReplacements = true;
                        if (toolName.Equals("update_plan_progress", StringComparison.Ordinal) &&
                            lower.Contains("missing parameter autoadvance"))
                            updatePlanMissingAutoAdvance = true;
                    }
                }
            }

            var parts = new List<string>();
            if (createFileMissingContent)
                parts.Add("For create_file, always provide both required fields: filePath and content. Do not call create_file with only filePath.");
            if (multiReplaceMissingExplanation || multiReplaceMissingReplacements)
                parts.Add("For multi_replace_string_in_file, always provide replacements as a non-empty array and explanation as a string. If you only have one replacement, use replace_string_in_file instead.");
            if (updatePlanMissingAutoAdvance)
                parts.Add("For update_plan_progress, always provide all required fields: stepId, status, message, and autoAdvance. Do not explain the schema error; retry the tool call with autoAdvance as a boolean.");

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        /// <summary>
        /// Adds a non-suppressing instruction when repeated file_search misses conflict with a create-file goal.
        /// </summary>
        private string AddCreateFileHintAfterRepeatedFileSearchMisses(string requestBody) {
            try {
                if (!HasCreateFileGoal(requestBody))
                    return requestBody;

                if (!IsToolAdvertised(requestBody, "create_file"))
                    return requestBody;

                int missCount = CountFileSearchMisses(requestBody);
                if (missCount < 5)
                    return requestBody;

                const string hintMarker = "[LmVsProxy create_file hint]";
                if (requestBody.Contains(hintMarker))
                    return requestBody;

                string hint = hintMarker + " file_search has failed to find the requested file " +
                              missCount.ToString() + " times. The user's request is a creation task. " +
                              "Do not keep searching for that missing file; call create_file with the intended path and content, then use edit/read/build tools as needed.";

                string rewritten = AppendSystemMessage(requestBody, hint);
                if (!ReferenceEquals(rewritten, requestBody))
                    LogMessage("[Create File Hint] Added create_file guidance after " + missCount.ToString() + " file_search miss(es).");

                return rewritten;
            } catch (Exception ex) {
                LogMessage("[Create File Hint] Error: " + ex.Message);
                return requestBody;
            }
        }

        /// <summary>
        /// Returns true when the last user request is asking to create/add/make something.
        /// </summary>
        private bool HasCreateFileGoal(string requestBody) {
            string lastUserMessage = ExtractLastUserMessage(requestBody);
            if (string.IsNullOrWhiteSpace(lastUserMessage))
                return false;

            string lower = lastUserMessage.ToLowerInvariant();
            return lower.Contains("create ") ||
                   lower.Contains("add ") ||
                   lower.Contains("make ") ||
                   lower.Contains("new file") ||
                   lower.Contains("new tool") ||
                   lower.Contains("scaffold");
        }

        /// <summary>
        /// Returns true when the latest build/get_errors failure has not been followed by a successful edit.
        /// </summary>
        private bool HasUnfixedBuildOrErrorFailure(string requestBody, out string errorFile) {
            errorFile = null;
            bool pendingFailure = false;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return false;

                    var toolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonElement message in messages.EnumerateArray()) {
                        string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                      roleElement.ValueKind == JsonValueKind.String
                            ? roleElement.GetString()
                            : "";

                        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                            message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array) {
                            foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                                string id = toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                            idElement.ValueKind == JsonValueKind.String
                                    ? idElement.GetString()
                                    : "";

                                string name = "";
                                if (toolCall.TryGetProperty("function", out JsonElement function) &&
                                    function.ValueKind == JsonValueKind.Object)
                                    name = ExtractStringProperty(function, "name") ?? "";

                                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                                    toolCallNames[id] = name;
                            }
                        } else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)) {
                            string toolCallId = message.TryGetProperty("tool_call_id", out JsonElement toolCallIdElement) &&
                                                toolCallIdElement.ValueKind == JsonValueKind.String
                                ? toolCallIdElement.GetString()
                                : "";

                            string toolName = !string.IsNullOrWhiteSpace(toolCallId) &&
                                              toolCallNames.TryGetValue(toolCallId, out string mappedName)
                                ? mappedName
                                : "";

                            string content = message.TryGetProperty("content", out JsonElement contentElement)
                                ? contentElement.ToString()
                                : "";

                            if (IsBuildErrorTool(toolName) && LooksLikeBuildOrGetErrorsFailure(content)) {
                                pendingFailure = true;
                                string extracted = ExtractFirstErrorFile(content);
                                if (!string.IsNullOrWhiteSpace(extracted))
                                    errorFile = extracted;
                                continue;
                            }

                            if (pendingFailure && IsEditTool(toolName) && !LooksLikeToolError(content)) {
                                pendingFailure = false;
                                errorFile = null;
                            }
                        }
                    }
                }
            } catch { }

            return pendingFailure;
        }

        /// <summary>
        /// Counts matching tool calls after the latest build/get_errors failure.
        /// </summary>
        private int CountToolCallsAfterLatestBuildFailure(string requestBody, string toolName, string targetFile) {
            int count = 0;
            bool afterFailure = false;
            string normalizedTarget = NormalizePathForCompare(targetFile ?? "");

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return 0;

                    var toolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonElement message in messages.EnumerateArray()) {
                        string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                      roleElement.ValueKind == JsonValueKind.String
                            ? roleElement.GetString()
                            : "";

                        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                            message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array) {
                            foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                                if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                                    function.ValueKind != JsonValueKind.Object)
                                    continue;

                                string id = toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                            idElement.ValueKind == JsonValueKind.String
                                    ? idElement.GetString()
                                    : "";
                                string name = ExtractStringProperty(function, "name") ?? "";
                                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                                    toolCallNames[id] = name;

                                if (!afterFailure || !name.Equals(toolName, StringComparison.Ordinal))
                                    continue;

                                string arguments = function.TryGetProperty("arguments", out JsonElement argumentsElement)
                                    ? argumentsElement.ToString()
                                    : "";

                                if (string.IsNullOrWhiteSpace(normalizedTarget) ||
                                    NormalizePathForCompare(arguments).Contains(normalizedTarget))
                                    count++;
                            }
                        } else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)) {
                            string toolCallId = message.TryGetProperty("tool_call_id", out JsonElement toolCallIdElement) &&
                                                toolCallIdElement.ValueKind == JsonValueKind.String
                                ? toolCallIdElement.GetString()
                                : "";

                            string mappedName = !string.IsNullOrWhiteSpace(toolCallId) &&
                                                toolCallNames.TryGetValue(toolCallId, out string name)
                                ? name
                                : "";

                            string content = message.TryGetProperty("content", out JsonElement contentElement)
                                ? contentElement.ToString()
                                : "";

                            if (IsBuildErrorTool(mappedName) && LooksLikeBuildOrGetErrorsFailure(content)) {
                                afterFailure = true;
                                count = 0;
                            } else if (afterFailure && IsEditTool(mappedName) && !LooksLikeToolError(content)) {
                                afterFailure = false;
                            }
                        }
                    }
                }
            } catch { }

            return afterFailure ? count : 0;
        }

        private bool IsBuildErrorTool(string toolName) {
            return toolName.Equals("run_build", StringComparison.Ordinal) ||
                   toolName.Equals("get_errors", StringComparison.Ordinal);
        }

        private bool IsEditTool(string toolName) {
            return toolName.Equals("replace_string_in_file", StringComparison.Ordinal) ||
                   toolName.Equals("multi_replace_string_in_file", StringComparison.Ordinal) ||
                   toolName.Equals("create_file", StringComparison.Ordinal) ||
                   toolName.Equals("remove_file", StringComparison.Ordinal) ||
                   toolName.Equals("run_command_in_terminal", StringComparison.Ordinal);
        }

        private bool LooksLikeBuildOrGetErrorsFailure(string content) {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string lower = content.ToLowerInvariant();
            return lower.Contains("build failed") ||
                   lower.Contains("error in file:") ||
                   lower.Contains("produces this issue") ||
                   lower.Contains("xls0") ||
                   lower.Contains("bc3") ||
                   lower.Contains("data at the root level is invalid");
        }

        private string ExtractFirstErrorFile(string content) {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(
                content,
                @"Error in File:\s*(?<file>[^\r\n]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return match.Success
                ? RepairPathControlCharacters(match.Groups["file"].Value.Trim())
                : null;
        }

        /// <summary>
        /// Counts file_search results that indicate no useful file was found.
        /// </summary>
        private int CountFileSearchMisses(string requestBody) {
            int misses = 0;

            using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                    messages.ValueKind != JsonValueKind.Array)
                    return 0;

                var toolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonElement message in messages.EnumerateArray()) {
                    string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                  roleElement.ValueKind == JsonValueKind.String
                        ? roleElement.GetString()
                        : "";

                    if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                        message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                        toolCalls.ValueKind == JsonValueKind.Array) {
                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            string id = toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                        idElement.ValueKind == JsonValueKind.String
                                ? idElement.GetString()
                                : "";
                            string name = "";
                            if (toolCall.TryGetProperty("function", out JsonElement function) &&
                                function.ValueKind == JsonValueKind.Object &&
                                function.TryGetProperty("name", out JsonElement nameElement) &&
                                nameElement.ValueKind == JsonValueKind.String) {
                                name = nameElement.GetString();
                            }

                            if (!string.IsNullOrWhiteSpace(id) &&
                                name.Equals("file_search", StringComparison.Ordinal))
                                toolCallNames[id] = name;
                        }
                    } else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)) {
                        string toolCallId = message.TryGetProperty("tool_call_id", out JsonElement toolCallIdElement) &&
                                            toolCallIdElement.ValueKind == JsonValueKind.String
                            ? toolCallIdElement.GetString()
                            : "";

                        if (string.IsNullOrWhiteSpace(toolCallId) || !toolCallNames.ContainsKey(toolCallId))
                            continue;

                        string content = message.TryGetProperty("content", out JsonElement contentElement)
                            ? contentElement.ToString()
                            : "";

                        if (LooksLikeFileSearchMiss(content))
                            misses++;
                    }
                }
            }

            return misses;
        }

        /// <summary>
        /// Detects file_search results that mean the target was not found.
        /// </summary>
        private bool LooksLikeFileSearchMiss(string content) {
            if (string.IsNullOrWhiteSpace(content))
                return true;

            string lower = content.ToLowerInvariant();
            return lower.Contains("no results") ||
                   lower.Contains("no files") ||
                   lower.Contains("not found") ||
                   lower.Contains("couldn't find") ||
                   lower.Contains("could not find") ||
                   lower.Contains("0 results") ||
                   lower.Trim().Equals("[]", StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true when a tool is still advertised in the current request.
        /// </summary>
        private bool IsToolAdvertised(string requestBody, string toolName) {
            return GetAvailableToolNames(requestBody).Contains(toolName);
        }

        /// <summary>
        /// Appends a system message to a chat completion request.
        /// </summary>
        private string AppendSystemMessage(string requestBody, string content) {
            using (JsonDocument document = JsonDocument.Parse(requestBody))
            using (var stream = new MemoryStream())
            using (var writer = new Utf8JsonWriter(stream)) {
                writer.WriteStartObject();
                foreach (JsonProperty property in document.RootElement.EnumerateObject()) {
                    if (property.NameEquals("messages") && property.Value.ValueKind == JsonValueKind.Array) {
                        writer.WritePropertyName("messages");
                        writer.WriteStartArray();
                        foreach (JsonElement message in property.Value.EnumerateArray())
                            message.WriteTo(writer);

                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", content);
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
                writer.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// Detects when a conversation has spent too many turns asking VS tools instead of producing an answer.
        /// </summary>
        private bool HasTooManyToolRounds(string requestBody, out int totalToolCalls) {
            totalToolCalls = 0;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return false;

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        if (!message.TryGetProperty("role", out JsonElement role) ||
                            role.ValueKind != JsonValueKind.String ||
                            !role.GetString().Equals("assistant", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array) {
                            totalToolCalls += toolCalls.GetArrayLength();
                        }
                    }
                }
            } catch (Exception ex) {
                LogMessage($"[Tool Rounds] Error counting: {ex.Message}");
                return false;
            }

            return totalToolCalls >= 20;
        }

        /// <summary>
        /// Gets every tool name already called by the assistant in this conversation.
        /// </summary>
        private HashSet<string> GetPreviouslyCalledToolNames(string requestBody) {
            var result = new HashSet<string>(StringComparer.Ordinal);

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return result;

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        if (!message.TryGetProperty("role", out JsonElement role) ||
                            role.ValueKind != JsonValueKind.String ||
                            !role.GetString().Equals("assistant", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!message.TryGetProperty("tool_calls", out JsonElement toolCalls) ||
                            toolCalls.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            if (toolCall.TryGetProperty("function", out JsonElement function) &&
                                function.ValueKind == JsonValueKind.Object &&
                                function.TryGetProperty("name", out JsonElement nameElement) &&
                                nameElement.ValueKind == JsonValueKind.String) {
                                string name = nameElement.GetString();
                                if (!string.IsNullOrWhiteSpace(name))
                                    result.Add(name);
                            }
                        }
                    }
                }
            } catch { }

            return result;
        }

        /// <summary>
        /// Only suppress broad discovery tools that have proven loop-prone. Actionable tools must stay available.
        /// </summary>
        private bool IsSuppressibleVsTool(string toolName) {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            return toolName.Equals("get_projects_in_solution", StringComparison.Ordinal) ||
                   toolName.Equals("code_search", StringComparison.Ordinal);
        }

        /// <summary>
        /// Removes actionable tools from a suppression set so reads, edits, terminal, and build can still run.
        /// </summary>
        private void KeepOnlySuppressibleVsTools(HashSet<string> toolNames) {
            if (toolNames == null || toolNames.Count == 0)
                return;

            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string toolName in toolNames) {
                if (IsSuppressibleVsTool(toolName))
                    allowed.Add(toolName);
            }

            toolNames.Clear();
            foreach (string toolName in allowed)
                toolNames.Add(toolName);
        }

        /// <summary>
        /// Finds Visual Studio tools that are stuck returning empty or repeated results.
        /// </summary>
        private bool TryGetToolsToSuppress(string requestBody, out HashSet<string> toolsToSuppress, out string reason) {
            toolsToSuppress = new HashSet<string>(StringComparer.Ordinal);
            reason = null;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return false;

                    var toolCallIds = new Dictionary<string, string>(StringComparer.Ordinal);
                    var toolCallCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var emptyToolResultCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var successfulToolResultCounts = new Dictionary<string, int>(StringComparer.Ordinal);

                    foreach (JsonElement message in messages.EnumerateArray()) {
                        string role = message.TryGetProperty("role", out JsonElement roleElement) &&
                                      roleElement.ValueKind == JsonValueKind.String
                            ? roleElement.GetString()
                            : "";

                        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                            message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array) {
                            foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                                string id = toolCall.TryGetProperty("id", out JsonElement idElement) &&
                                            idElement.ValueKind == JsonValueKind.String
                                    ? idElement.GetString()
                                    : "";

                                if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                                    function.ValueKind != JsonValueKind.Object)
                                    continue;

                                string name = function.TryGetProperty("name", out JsonElement nameElement) &&
                                              nameElement.ValueKind == JsonValueKind.String
                                    ? nameElement.GetString()
                                    : "";

                                if (string.IsNullOrWhiteSpace(name))
                                    continue;

                                if (!string.IsNullOrEmpty(id))
                                    toolCallIds[id] = name;

                                toolCallCounts.TryGetValue(name, out int callCount);
                                toolCallCounts[name] = callCount + 1;
                            }
                        } else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)) {
                            string toolCallId = message.TryGetProperty("tool_call_id", out JsonElement toolCallIdElement) &&
                                                toolCallIdElement.ValueKind == JsonValueKind.String
                                ? toolCallIdElement.GetString()
                                : "";

                            if (string.IsNullOrEmpty(toolCallId) || !toolCallIds.TryGetValue(toolCallId, out string toolName))
                                continue;

                            string content = message.TryGetProperty("content", out JsonElement contentElement)
                                ? contentElement.ToString()
                                : "";

                            if (string.IsNullOrWhiteSpace(content)) {
                                emptyToolResultCounts.TryGetValue(toolName, out int emptyCount);
                                emptyToolResultCounts[toolName] = emptyCount + 1;
                            } else {
                                successfulToolResultCounts.TryGetValue(toolName, out int successCount);
                                successfulToolResultCounts[toolName] = successCount + 1;
                            }
                        }
                    }

                    foreach (var kv in emptyToolResultCounts) {
                        if (kv.Key.Equals("get_projects_in_solution", StringComparison.Ordinal) && kv.Value >= 1)
                            toolsToSuppress.Add(kv.Key);
                        else if (kv.Key.Equals("code_search", StringComparison.Ordinal) && kv.Value >= 3)
                            toolsToSuppress.Add(kv.Key);
                    }

                    foreach (var kv in toolCallCounts) {
                        if (kv.Key.Equals("get_projects_in_solution", StringComparison.Ordinal) && kv.Value >= 2)
                            toolsToSuppress.Add(kv.Key);
                        else if (kv.Key.Equals("code_search", StringComparison.Ordinal) && kv.Value >= 5)
                            toolsToSuppress.Add(kv.Key);
                    }

                    if (successfulToolResultCounts.ContainsKey("get_projects_in_solution"))
                        toolsToSuppress.Add("get_projects_in_solution");

                    KeepOnlySuppressibleVsTools(toolsToSuppress);

                    if (toolsToSuppress.Count == 0)
                        return false;

                    reason = "Some Visual Studio tools already returned empty or repeated results and must not be called again this turn: " +
                             string.Join(", ", toolsToSuppress) +
                             ". Continue with the remaining tools. Exact file reads, file search, terminal commands, edits, and build verification are still available.";
                    return true;
                }
            } catch (Exception ex) {
                LogMessage($"[Tool Detection] Error detecting failed tools: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes known-stuck tools while keeping the rest of Visual Studio's tool surface available.
        /// </summary>
        private string RewriteChatCompletionWithSuppressedTools(string requestBody, HashSet<string> toolsToSuppress, string reason) {
            if (toolsToSuppress == null || toolsToSuppress.Count == 0)
                return requestBody;

            KeepOnlySuppressibleVsTools(toolsToSuppress);
            if (toolsToSuppress.Count == 0)
                return requestBody;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody))
                using (var stream = new MemoryStream())
                using (var writer = new Utf8JsonWriter(stream)) {
                    bool sawTools = false;
                    bool anyToolsRemaining = false;
                    bool wroteToolChoice = false;
                    bool suppressedAnyTool = false;

                    writer.WriteStartObject();
                    foreach (JsonProperty property in document.RootElement.EnumerateObject()) {
                        if (property.NameEquals("tools") && property.Value.ValueKind == JsonValueKind.Array) {
                            sawTools = true;
                            writer.WritePropertyName("tools");
                            writer.WriteStartArray();
                            foreach (JsonElement tool in property.Value.EnumerateArray()) {
                                string name = "";
                                if (tool.TryGetProperty("function", out JsonElement function) &&
                                    function.ValueKind == JsonValueKind.Object &&
                                    function.TryGetProperty("name", out JsonElement nameElement) &&
                                    nameElement.ValueKind == JsonValueKind.String) {
                                    name = nameElement.GetString();
                                }

                                if (!string.IsNullOrWhiteSpace(name) && toolsToSuppress.Contains(name)) {
                                    suppressedAnyTool = true;
                                    continue;
                                }

                                tool.WriteTo(writer);
                                anyToolsRemaining = true;
                            }
                            writer.WriteEndArray();
                            continue;
                        }

                        if (property.NameEquals("tool_choice")) {
                            if (sawTools && !anyToolsRemaining)
                                writer.WriteString("tool_choice", "auto");
                            else
                                property.WriteTo(writer);
                            wroteToolChoice = true;
                            continue;
                        }

                        if (property.NameEquals("messages") && property.Value.ValueKind == JsonValueKind.Array) {
                            writer.WritePropertyName("messages");
                            writer.WriteStartArray();
                            foreach (JsonElement message in property.Value.EnumerateArray()) {
                                message.WriteTo(writer);
                            }

                            writer.WriteStartObject();
                            writer.WriteString("role", "system");
                            writer.WriteString("content", reason);
                            writer.WriteEndObject();
                            writer.WriteEndArray();
                            continue;
                        }

                        property.WriteTo(writer);
                    }

                    if (!wroteToolChoice && sawTools && !anyToolsRemaining)
                        writer.WriteString("tool_choice", "auto");

                    writer.WriteEndObject();
                    writer.Flush();

                    if (!suppressedAnyTool)
                        return requestBody;

                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            } catch (Exception ex) {
                LogMessage($"[Tool Suppression] Error: {ex.Message}");
                return requestBody;
            }
        }

        /// <summary>
        /// Detects when Visual Studio has already executed the same tool call repeatedly.
        /// </summary>
        private bool HasRepeatedToolCallLoop(string requestBody, out string repeatedToolName, out int repeatCount) {
            repeatedToolName = null;
            repeatCount = 0;

            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody)) {
                    if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
                        messages.ValueKind != JsonValueKind.Array)
                        return false;

                    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (JsonElement message in messages.EnumerateArray()) {
                        if (!message.TryGetProperty("role", out JsonElement role) ||
                            role.ValueKind != JsonValueKind.String ||
                            !role.GetString().Equals("assistant", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!message.TryGetProperty("tool_calls", out JsonElement toolCalls) ||
                            toolCalls.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement toolCall in toolCalls.EnumerateArray()) {
                            if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                                function.ValueKind != JsonValueKind.Object)
                                continue;

                            string name = function.TryGetProperty("name", out JsonElement nameElement) &&
                                          nameElement.ValueKind == JsonValueKind.String
                                ? nameElement.GetString()
                                : "";
                            string arguments = function.TryGetProperty("arguments", out JsonElement argumentsElement)
                                ? argumentsElement.ToString()
                                : "";
                            string signature = name + "\n" + arguments;

                            counts.TryGetValue(signature, out int currentCount);
                            currentCount++;
                            counts[signature] = currentCount;

                            if (currentCount > repeatCount) {
                                repeatedToolName = name;
                                repeatCount = currentCount;
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                LogMessage($"[Tool Loop] Error detecting: {ex.Message}");
                return false;
            }

            if (repeatedToolName != null &&
                repeatedToolName.Equals("get_projects_in_solution", StringComparison.Ordinal))
                return repeatCount >= 3;

            if (repeatedToolName != null &&
                repeatedToolName.Equals("code_search", StringComparison.Ordinal))
                return repeatCount >= 5;

            return false;
        }

        /// <summary>
        /// Removes tools for one turn and asks the model to answer using the gathered context.
        /// </summary>
        private string RewriteChatCompletionWithoutTools(string requestBody, string repeatedToolName, int repeatCount) {
            try {
                using (JsonDocument document = JsonDocument.Parse(requestBody))
                using (var stream = new MemoryStream())
                using (var writer = new Utf8JsonWriter(stream)) {
                    bool wroteToolChoice = false;

                    writer.WriteStartObject();
                    foreach (JsonProperty property in document.RootElement.EnumerateObject()) {
                        if (property.NameEquals("tools"))
                            continue;

                        if (property.NameEquals("tool_choice")) {
                            writer.WriteString("tool_choice", "none");
                            wroteToolChoice = true;
                            continue;
                        }

                        if (property.NameEquals("messages") && property.Value.ValueKind == JsonValueKind.Array) {
                            writer.WritePropertyName("messages");
                            writer.WriteStartArray();
                            foreach (JsonElement message in property.Value.EnumerateArray()) {
                                message.WriteTo(writer);
                            }

                            writer.WriteStartObject();
                            writer.WriteString("role", "system");
                            writer.WriteString("content",
                                "The same Visual Studio tool call has already been repeated (" +
                                repeatedToolName + ", " + repeatCount.ToString() +
                                " times). Do not call tools again in this turn and do not emit <tool_call> tags. Produce normal assistant text only. Use the existing file, IDE, and tool-result context to provide the final answer or concise next edit step.");
                            writer.WriteEndObject();
                            writer.WriteEndArray();
                            continue;
                        }

                        property.WriteTo(writer);
                    }

                    if (!wroteToolChoice)
                        writer.WriteString("tool_choice", "none");

                    writer.WriteEndObject();
                    writer.Flush();

                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            } catch (Exception ex) {
                LogMessage($"[Tool Loop] Error rewriting request: {ex.Message}");
                return requestBody;
            }
        }

        /// <summary>
        /// Copies supported headers from an upstream HttpResponseMessage to HttpListenerResponse.
        /// </summary>
        private void CopyResponseHeaders(HttpResponseMessage source, HttpListenerResponse target) {
            if (source == null || target == null)
                return;

            if (source.Content?.Headers?.ContentType != null) {
                target.ContentType = source.Content.Headers.ContentType.ToString();
            }

            if (source.Content?.Headers?.ContentLength.HasValue == true) {
                target.ContentLength64 = source.Content.Headers.ContentLength.Value;
            }

            foreach (var header in source.Headers) {
                if (header.Key == null)
                    continue;
                if (IsRestrictedResponseHeader(header.Key))
                    continue;
                target.Headers[header.Key] = string.Join(", ", header.Value);
            }

            if (source.Content != null) {
                foreach (var header in source.Content.Headers) {
                    if (header.Key == null)
                        continue;
                    if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsRestrictedResponseHeader(header.Key))
                        continue;
                    target.Headers[header.Key] = string.Join(", ", header.Value);
                }
            }
        }

        /// <summary>
        /// Determines whether a response header is restricted by HttpListenerResponse.
        /// </summary>
        private bool IsRestrictedResponseHeader(string headerName) {
            if (string.IsNullOrWhiteSpace(headerName))
                return true;

            return WebHeaderCollection.IsRestricted(headerName);
        }

        /// <summary>
        /// Converts an LM Studio response to VS format.
        /// </summary>
        private string ConvertLmResponseToVsFormat(string lmResponse) {
            // Extract the first choice's message from LM Studio response
            // LM Studio format: {"choices": [{"message": {"content": "...", "function_call": {...}}}]}
            // VS format: {"tool_calls": [{"id": "...", "type": "function", "function": {...}}], "content": "..."}

            try {
                var choiceMatch = System.Text.RegularExpressions.Regex.Match(lmResponse, @"""message""\s*:\s*(\{[^}]*\})");
                if (choiceMatch.Success) {
                    string message = choiceMatch.Groups[1].Value;

                    // Check for function_call
                    if (message.Contains("\"function_call\"")) {
                        var funcMatch = System.Text.RegularExpressions.Regex.Match(message, @"""function_call""\s*:\s*(\{[^}]*\})");
                        if (funcMatch.Success) {
                            string funcCall = funcMatch.Groups[1].Value;
                            string toolId = "tool_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                            return "{ \"tool_calls\": [{ \"id\": \"" + toolId + "\", \"type\": \"function\", \"function\": " + funcCall + " }] }";
                        }
                    }

                    // Extract content
                    var contentMatch = System.Text.RegularExpressions.Regex.Match(message, @"""content""\s*:\s*""([^""]*)""");
                    if (contentMatch.Success) {
                        string content = EscapeJson(contentMatch.Groups[1].Value);
                        return "{ \"content\": \"" + content + "\", \"tool_calls\": null }";
                    }
                }

                return "{ \"content\": \"No response from LM Studio\", \"tool_calls\": null }";
            } catch (Exception ex) {
                LogMessage($"[Response Conversion] Error: {ex.Message}");
                return "{ \"error\": \"Failed to parse LM Studio response\", \"tool_calls\": null }";
            }
        }

        /// <summary>
        /// Escapes a string for JSON.
        /// </summary>
        private string EscapeJson(string text) {
            if (text == null)
                return "";

            var sb = new StringBuilder(text.Length + 16);
            foreach (char ch in text) {
                switch (ch) {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 0x20)
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Truncates diagnostic text so OutputLog stays useful and responsive.
        /// </summary>
        private string TruncateForLog(string text, int maxChars) {
            if (string.IsNullOrEmpty(text))
                return "";
            if (maxChars <= 0 || text.Length <= maxChars)
                return EscapeControlCharsForLog(text);

            return EscapeControlCharsForLog(text.Substring(0, maxChars)) + "...";
        }

        /// <summary>
        /// Makes control characters visible in logs so hidden bytes are diagnosable.
        /// </summary>
        private string EscapeControlCharsForLog(string text) {
            if (string.IsNullOrEmpty(text))
                return "";

            var sb = new StringBuilder(text.Length);
            foreach (char ch in text) {
                switch (ch) {
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 0x20)
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }

            return sb.ToString();
        }

        public void Dispose() {
            if (_isDisposed)
                return;

            Stop();
            _httpListener = null;
            try {
                (_chatServer as IDisposable)?.Dispose();
            } catch { }
            _chatServer = null;
            _httpClient?.Dispose();
            _cts?.Dispose();
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Supporting Classes

        /// <summary>
        /// Represents a request from Visual Studio.
        /// </summary>
        private class VsRequest {
            public string model { get; set; }
            public List<Dictionary<string, object>> messages { get; set; }
            public List<Dictionary<string, object>> tools { get; set; }
            public double? temperature { get; set; }
            public double? top_p { get; set; }
            public int? max_tokens { get; set; }
            public string rawJson { get; set; }
        }

        /// <summary>
        /// Represents a parsed Qwen raw tool call.
        /// </summary>
        private class ToolCallData {
            public string Name { get; set; }
            public string ArgumentsJson { get; set; }
        }

        /// <summary>
        /// Accumulates fragmented native OpenAI streaming tool-call deltas.
        /// </summary>
        private class ToolCallBuilder {
            public string Id { get; set; }
            public string Name { get; set; }
            public StringBuilder Arguments { get; } = new StringBuilder();
        }

        private class ChatUiCompletion {
            public string Content { get; set; } = "";
            public string Reasoning { get; set; } = "";
        }

        #endregion
    }

    /// <summary>
    /// Event arguments for OutputLog events from LmVsProxy.
    /// </summary>
    public class OutputLogEventArgs : EventArgs {
        public string Message { get; set; }

        public OutputLogEventArgs(string message) {
            Message = message;
        }
    }
}
