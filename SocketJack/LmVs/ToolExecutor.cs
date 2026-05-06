using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LmVs
{
    // Minimal ITool interface used in this example. Replace with your actual tool interface.
    public interface ITool
    {
        string Id { get; }
        string Name { get; }
        Task<string> RunAsync(string argsJson);
    }

    public class ToolExecutor
    {
        private readonly UpstreamAdapter _upstreamAdapter;
        private readonly Stream _clientStream;
        private readonly CancellationToken _cancellationToken;

        public ToolExecutor(UpstreamAdapter upstreamAdapter, Stream clientStream, CancellationToken cancellationToken)
        {
            _upstreamAdapter = upstreamAdapter;
            _clientStream = clientStream;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Executes a tool and forwards the final tool output to the model via UpstreamAdapter.
        /// Replace existing forwarding logic with this pattern so the model receives a single
        /// assistant message containing the tool output, followed by a final stop chunk and [DONE].
        /// </summary>
        public async Task<string> ExecuteToolAsync(ITool tool, string argsJson)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));

            // Generate or use existing tool call id
            var toolCallId = string.IsNullOrEmpty(tool.Id) ? Guid.NewGuid().ToString() : tool.Id;
            var toolName = tool.Name ?? "unknown_tool";

            // Run the tool and capture final textual output
            // If your tool streams partial output, you may stream partials separately here.
            var finalOutput = await tool.RunAsync(argsJson).ConfigureAwait(false);

            // IMPORTANT: forward only the final tool output (not the original user prompt or args)
            await _upstreamAdapter.HandleToolResultAsync(toolCallId, toolName, finalOutput, _clientStream, _cancellationToken).ConfigureAwait(false);

            return finalOutput;
        }
    }
}