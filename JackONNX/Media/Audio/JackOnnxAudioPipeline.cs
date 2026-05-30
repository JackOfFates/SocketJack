using JackONNX;
using JackONNX.Runtime;

namespace JackONNX.Audio;

public sealed class JackOnnxAudioPipeline
{
    private readonly JackOnnxRuntimeEngine _runtime;

    public JackOnnxAudioPipeline(JackOnnxRuntimeEngine runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<JackOnnxGenerationResult> GenerateAsync(
        AudioGenerationRequest request,
        IProgress<JackOnnxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt) && string.IsNullOrWhiteSpace(request.SourcePath) && string.IsNullOrWhiteSpace(request.SourceDataUrl))
            throw new ArgumentException("Audio prompt or source audio is required.", nameof(request));

        cancellationToken.ThrowIfCancellationRequested();
        var job = _runtime.CreateJob(JackOnnxMediaKind.Audio, request);
        string sourceDetail = string.IsNullOrWhiteSpace(request.SourcePath)
            ? (string.IsNullOrWhiteSpace(request.SourceDataUrl) ? "" : " Source data URL was provided.")
            : " Source: " + request.SourcePath;
        progress?.Report(new JackOnnxProgress
        {
            JobId = job.Id,
            State = JackOnnxJobState.Queued,
            Percent = 0,
            Message = "Audio generation job accepted. ONNX/PyTorch audio execution is the next implementation step." + sourceDetail
        });

        var result = new JackOnnxGenerationResult
        {
            JobId = job.Id,
            Success = false,
            Message = "Audio generation pipeline is wired for source media, but ONNX/PyTorch audio model execution is not implemented yet." + sourceDetail
        };
        _runtime.CompleteJob(job.Id, result);
        return Task.FromResult(result);
    }

    public Task<JackOnnxGenerationResult> GenerateSpeechAsync(
        SpeechGenerationRequest request,
        IProgress<JackOnnxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Speech text is required.", nameof(request));

        cancellationToken.ThrowIfCancellationRequested();
        var job = _runtime.CreateJob(JackOnnxMediaKind.Audio, request);
        progress?.Report(new JackOnnxProgress
        {
            JobId = job.Id,
            State = JackOnnxJobState.Queued,
            Percent = 0,
            Message = "Speech generation job accepted. ONNX TTS execution is the next implementation step."
        });

        var result = new JackOnnxGenerationResult
        {
            JobId = job.Id,
            Success = false,
            Message = "Audio pipeline scaffold is ready; model execution is not implemented yet."
        };
        _runtime.CompleteJob(job.Id, result);
        return Task.FromResult(result);
    }
}
