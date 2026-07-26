namespace JackLLM.Mobile.Services;

public sealed class MobileAudioService
{
#if ANDROID
    private global::Android.Media.MediaRecorder? _recorder;
    private string _path = "";
#elif IOS
    private global::AVFoundation.AVAudioRecorder? _recorder;
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
#elif IOS
        _path = Path.Combine(FileSystem.CacheDirectory, "voice-" + Guid.NewGuid().ToString("N") + ".m4a");
        global::AVFoundation.AVAudioSession.SharedInstance().SetCategory(
            global::AVFoundation.AVAudioSessionCategory.PlayAndRecord,
            global::AVFoundation.AVAudioSessionCategoryOptions.DefaultToSpeaker);
        global::AVFoundation.AVAudioSession.SharedInstance().SetActive(true);
        var settings = new global::AVFoundation.AudioSettings
        {
            Format = global::AudioToolbox.AudioFormatType.MPEG4AAC,
            SampleRate = 44100,
            NumberChannels = 1,
            EncoderBitRate = 96000,
            AudioQuality = global::AVFoundation.AVAudioQuality.High
        };
        _recorder = global::AVFoundation.AVAudioRecorder.Create(
            global::Foundation.NSUrl.FromFilename(_path), settings, out global::Foundation.NSError? error);
        if (_recorder is null || error is not null)
            throw new InvalidOperationException(error?.LocalizedDescription ?? "iOS could not start voice recording.");
        if (!_recorder.PrepareToRecord() || !_recorder.Record())
            throw new InvalidOperationException("iOS could not start voice recording.");
        IsRecording = true;
        return Task.CompletedTask;
#else
        throw new PlatformNotSupportedException("Voice recording is unavailable on this platform.");
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
#elif IOS
        try { _recorder?.Stop(); }
        finally { _recorder?.Dispose(); _recorder = null; IsRecording = false; }
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
#elif IOS
        using global::Foundation.NSData data = global::Foundation.NSData.FromArray(audio);
        using global::AVFoundation.AVAudioPlayer? player = global::AVFoundation.AVAudioPlayer.FromData(data, out global::Foundation.NSError? error);
        if (player is null || error is not null)
            throw new InvalidOperationException(error?.LocalizedDescription ?? "iOS could not play the generated speech.");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FinishedPlaying += (_, _) => completion.TrySetResult(true);
        if (!player.Play()) throw new InvalidOperationException("iOS could not play the generated speech.");
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            player.Stop();
            completion.TrySetCanceled(cancellationToken);
        });
        await completion.Task;
#else
        await Task.CompletedTask;
#endif
    }
}
