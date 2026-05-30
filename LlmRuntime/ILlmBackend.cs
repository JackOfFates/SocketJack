namespace LlmRuntime;

public interface ILlmBackend : IAsyncDisposable, IDisposable
{
    string InstanceId { get; }

    string ModelPath { get; }

    LlmLoadConfig LoadConfig { get; }

    bool IsPromptPipelineReady { get; }

    string PromptPipelineStatus { get; }

    string PromptPipelineDetail { get; }

    DateTimeOffset? PromptPipelineReadyAtUtc { get; }

    double PromptPipelineWarmupSeconds { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task WarmPromptPipelineAsync(CancellationToken cancellationToken = default);

    Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmChatToken> StreamChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default);
}

public interface ILlmBackendFactory
{
    ILlmBackend Create(string instanceId, string modelPath, LlmLoadConfig loadConfig);
}
