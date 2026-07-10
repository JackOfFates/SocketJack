namespace JackLLM.Mobile.Services;

public sealed class MobileAudioService
{
#if ANDROID
    private global::Android.Media.MediaRecorder? _recorder;
    private string _path = "";
#endif
    public bool IsRecording { get; private set; }

    public Task StartAsync()
    {
        if (IsRecording) return Task.CompletedTask;
#if ANDROID
        _path = Path.Combine(FileSystem.CacheDirectory, "voice-" + Guid.NewGuid().ToString("N") + ".m4a");
#pragma warning disable CS0618, CA1422
        _recorder = new global::Android.Media.MediaRecorder();
#pragma warning restore CS0618, CA1422
        _recorder.SetAudioSource(global::Android.Media.AudioSource.Mic);
        _recorder.SetOutputFormat(global::Android.Media.OutputFormat.Mpeg4);
        _recorder.SetAudioEncoder(global::Android.Media.AudioEncoder.Aac);
        _recorder.SetAudioEncodingBitRate(96000);
        _recorder.SetAudioSamplingRate(44100);
        _recorder.SetOutputFile(_path);
        _recorder.Prepare();
        _recorder.Start();
        IsRecording = true;
        return Task.CompletedTask;
#else
        throw new PlatformNotSupportedException("Voice recording is available on Android.");
#endif
    }

    public Task<byte[]> StopAsync()
    {
        if (!IsRecording) return Task.FromResult(Array.Empty<byte>());
#if ANDROID
        try { _recorder?.Stop(); }
        finally { _recorder?.Reset(); _recorder?.Release(); _recorder?.Dispose(); _recorder = null; IsRecording = false; }
        byte[] bytes = File.ReadAllBytes(_path);
        try { File.Delete(_path); } catch { }
        return Task.FromResult(bytes);
#else
        return Task.FromResult(Array.Empty<byte>());
#endif
    }

    public async Task PlayAsync(byte[] audio, CancellationToken cancellationToken = default)
    {
#if ANDROID
        string path = Path.Combine(FileSystem.CacheDirectory, "speech-" + Guid.NewGuid().ToString("N") + ".mp3");
        await File.WriteAllBytesAsync(path, audio, cancellationToken);
        var completion = new TaskCompletionSource<bool>();
        using var player = new global::Android.Media.MediaPlayer();
        player.Completion += (_, _) => completion.TrySetResult(true);
        player.Error += (_, args) => { completion.TrySetException(new InvalidOperationException("Android could not play the generated speech.")); args.Handled = true; };
        player.SetDataSource(path); player.Prepare(); player.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await completion.Task;
        try { File.Delete(path); } catch { }
#else
        await Task.CompletedTask;
#endif
    }
}
