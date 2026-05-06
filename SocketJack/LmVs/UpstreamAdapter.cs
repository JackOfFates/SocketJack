// LmVsProxy.UpstreamAdapter.cs
// Purpose: Normalize tool results into a single assistant message, add idempotency guard,
// debounce repeated identical file edits, and ensure final stop chunk + data:[DONE] are sent.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LmVs
{
    public class UpstreamAdapter
    {
        private readonly string _upstreamModelName;
        private readonly ConcurrentDictionary<string, DateTime> _recentToolCalls = new();
        private readonly TimeSpan _duplicateToolWindow = TimeSpan.FromSeconds(5);

        // Debounce identical replace_string_in_file calls per (filePath + oldStringHash)
        private readonly ConcurrentDictionary<string, DateTime> _recentFileEdits = new();
        private readonly TimeSpan _fileEditDebounceWindow = TimeSpan.FromSeconds(2);

        public UpstreamAdapter(string upstreamModelName)
        {
            _upstreamModelName = string.IsNullOrEmpty(upstreamModelName) ? "unknown-model" : upstreamModelName;
        }

        /// <summary>
        /// Returns false when a tool call has already been forwarded recently.
        /// Use this before emitting tool-call SSE deltas to Visual Studio.
        /// </summary>
        public bool ShouldForwardToolCall(string toolCallId, string toolName, string argumentsJson)
        {
            if (string.IsNullOrEmpty(toolCallId))
                toolCallId = toolName + ":" + ComputeHashKey(toolName, argumentsJson);

            if (_recentToolCalls.TryGetValue(toolCallId, out var last) &&
                (DateTime.UtcNow - last) < _duplicateToolWindow)
            {
                LogDebug($"Debounced duplicate tool call id {toolCallId}");
                return false;
            }

            _recentToolCalls[toolCallId] = DateTime.UtcNow;

            if (!IsFileEditTool(toolName))
                return true;

            var key = ComputeFileEditKey(toolName, argumentsJson);
            if (_recentFileEdits.TryGetValue(key, out var lastEdit) &&
                (DateTime.UtcNow - lastEdit) < _fileEditDebounceWindow)
            {
                LogDebug($"Debounced duplicate file edit for key {key}");
                return false;
            }

            _recentFileEdits[key] = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Call this method when a tool finishes executing and you need to forward the tool result
        /// back into the LM Studio streaming response in the exact shape the model expects.
        /// </summary>
        /// <param name="toolCallId">Unique id for the tool invocation (if null a GUID will be generated)</param>
        /// <param name="toolName">Registered tool name (must match model's tool name exactly)</param>
        /// <param name="toolOutput">Tool output text (plain text or JSON depending on tool)</param>
        /// <param name="clientStream">Downstream client stream (SSE) to write frames to</param>
        /// <param name="ct">Cancellation token</param>
        public async Task HandleToolResultAsync(string toolCallId, string toolName, string toolOutput, Stream clientStream, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(toolCallId)) toolCallId = Guid.NewGuid().ToString();

            // Idempotency guard: ignore duplicate tool results for the same call within a short window
            if (_recentToolCalls.TryGetValue(toolCallId, out var last) && (DateTime.UtcNow - last) < _duplicateToolWindow)
            {
                LogDebug($"Ignoring duplicate tool result for id {toolCallId}");
                return;
            }
            _recentToolCalls[toolCallId] = DateTime.UtcNow;

            // Debounce identical file-edit tools to avoid repeated replace_string_in_file calls
            if (IsFileEditTool(toolName))
            {
                var key = ComputeHashKey(toolName, toolOutput);
                if (_recentFileEdits.TryGetValue(key, out var lastEdit) && (DateTime.UtcNow - lastEdit) < _fileEditDebounceWindow)
                {
                    LogDebug($"Debounced duplicate file edit for key {key}");
                    return;
                }
                _recentFileEdits[key] = DateTime.UtcNow;
            }

            // Build the assistant message object expected by LM Studio / client
            var assistantMessage = new
            {
                role = "assistant",
                content = new
                {
                    text = toolOutput ?? string.Empty
                },
                metadata = new
                {
                    tool_name = toolName ?? string.Empty,
                    tool_call_id = toolCallId,
                    tool_finished = true
                }
            };

            // Create a streaming chunk that contains the assistant message as a single chunk
            var chunk = new
            {
                id = Guid.NewGuid().ToString(),
                @object = "chat.completion.chunk",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = _upstreamModelName,
                choices = new[]
                {
                    new {
                        delta = new { message = assistantMessage },
                        index = 0,
                        finish_reason = (string?)null
                    }
                }
            };

            var payload = JsonSerializer.Serialize(chunk);
            await WriteSseFrameAsync(clientStream, payload, ct).ConfigureAwait(false);

            // Send a final chunk indicating the assistant message is complete with finish_reason = "stop"
            var finalChunk = new
            {
                id = Guid.NewGuid().ToString(),
                @object = "chat.completion.chunk",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = _upstreamModelName,
                choices = new[]
                {
                    new {
                        delta = new { },
                        index = 0,
                        finish_reason = "stop"
                    }
                }
            };

            var finalPayload = JsonSerializer.Serialize(finalChunk);
            await WriteSseFrameAsync(clientStream, finalPayload, ct).ConfigureAwait(false);

            // Send the standard done marker so the client knows streaming ended for this response
            await WriteSseDoneAsync(clientStream, ct).ConfigureAwait(false);

            LogInformation($"Forwarded tool result for {toolName} (id {toolCallId}) to model");
        }

        /// <summary>
        /// Writes a single SSE frame to the client stream in the format: data: {json}\n\n
        /// </summary>
        private async Task WriteSseFrameAsync(Stream stream, string jsonPayload, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (jsonPayload == null) jsonPayload = "{}";

            var s = $"data: {jsonPayload}\n\n";
            var bytes = Encoding.UTF8.GetBytes(s);
            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes the SSE done marker: data: [DONE]\n\n
        /// </summary>
        private async Task WriteSseDoneAsync(Stream stream, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var done = "data: [DONE]\n\n";
            var bytes = Encoding.UTF8.GetBytes(done);
            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Small helper to compute a stable key for debouncing repeated file edits.
        /// </summary>
        private static string ComputeHashKey(string toolName, string toolOutput)
        {
            var snippet = toolOutput ?? string.Empty;
            if (snippet.Length > 200) snippet = snippet.Substring(0, 200);
            return $"{toolName}:{snippet.GetHashCode():X8}";
        }

        private static bool IsFileEditTool(string toolName)
        {
            return !string.IsNullOrEmpty(toolName) &&
                   toolName.Equals("replace_string_in_file", StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeFileEditKey(string toolName, string argumentsJson)
        {
            string filePath = ExtractJsonStringProperty(argumentsJson, "filePath") ??
                              ExtractJsonStringProperty(argumentsJson, "filename") ??
                              ExtractJsonStringProperty(argumentsJson, "path") ??
                              "";
            string oldString = ExtractJsonStringProperty(argumentsJson, "oldString") ?? "";
            string newString = ExtractJsonStringProperty(argumentsJson, "newString") ?? "";
            string normalized = (filePath + "|" + oldString + "|" + newString).Replace("\r\n", "\n").Replace('\r', '\n');

            if (string.IsNullOrWhiteSpace(normalized))
                normalized = argumentsJson ?? "";

            return ComputeHashKey(toolName, normalized);
        }

        private static string ExtractJsonStringProperty(string json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
                return null;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        !document.RootElement.TryGetProperty(propertyName, out JsonElement value))
                        return null;

                    return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                }
            }
            catch
            {
                return null;
            }
        }

        #region Logging helpers (replace with your logging framework)

        private void LogDebug(string message)
        {
            try { Console.WriteLine($"[UpstreamAdapter][DEBUG] {message}"); } catch { }
        }

        private void LogInformation(string message)
        {
            try { Console.WriteLine($"[UpstreamAdapter][INFO] {message}"); } catch { }
        }

        #endregion
    }
}
