using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net
{
    public interface ILmVsProxyModelRuntime
    {
        string DisplayName { get; }

        string OpenAiBaseUrl { get; }

        Task EnsureStartedAsync(CancellationToken cancellationToken = default(CancellationToken));
    }

    public sealed class LmVsProxyModelRuntimeFallbackReport
    {
        public string Id { get; set; } = "";

        public string CreatedUtc { get; set; } = "";

        public string Severity { get; set; } = "critical";

        public string Category { get; set; } = "model-runtime-fallback";

        public string PrimaryProvider { get; set; } = "";

        public string FallbackProvider { get; set; } = "";

        public string Operation { get; set; } = "";

        public string Reason { get; set; } = "";

        public string Detail { get; set; } = "";

        public string MachineName { get; set; } = "";

        public string ServerId { get; set; } = "";

        public string ServerName { get; set; } = "";

        public string OwnerUserName { get; set; } = "";

        public string PublicHost { get; set; } = "";

        public string ReportedByUserName { get; set; } = "";

        public string SelectedProvider { get; set; } = "";

        public string ClientName { get; set; } = "LmVsProxy";
    }

    public sealed class HttpLmVsProxyModelRuntime : ILmVsProxyModelRuntime
    {
        public HttpLmVsProxyModelRuntime(string displayName, string openAiBaseUrl, System.Func<Task> ensureStartedAsync = null)
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "LM Studio" : displayName.Trim();
            OpenAiBaseUrl = LmVsProxyRemoteModelServerSelection.NormalizeOpenAiBaseUrl(openAiBaseUrl);
            _ensureStartedAsync = ensureStartedAsync;
        }

        private readonly System.Func<Task> _ensureStartedAsync;

        public string DisplayName { get; }

        public string OpenAiBaseUrl { get; }

        public async Task EnsureStartedAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ensureStartedAsync == null)
                return;

            Task task = _ensureStartedAsync();
            if (task != null)
                await task.ConfigureAwait(false);
        }
    }
}
