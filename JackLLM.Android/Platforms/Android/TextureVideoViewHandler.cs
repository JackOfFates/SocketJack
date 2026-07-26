using Android.Content;
using Android.Views;
using LibVLCSharp.Shared;
using Microsoft.Maui.Handlers;
using Org.Videolan.Libvlc;

namespace JackLLM.Mobile.Platforms.Android;

public sealed class TextureVideoViewHandler : ViewHandler<LibVLCSharp.MAUI.VideoView, TextureVlcView>
{
    public static readonly IPropertyMapper<LibVLCSharp.MAUI.VideoView, TextureVideoViewHandler> Mapper =
        new PropertyMapper<LibVLCSharp.MAUI.VideoView, TextureVideoViewHandler>(ViewMapper)
        {
            [nameof(LibVLCSharp.MAUI.VideoView.MediaPlayer)] = MapMediaPlayer
        };

    public TextureVideoViewHandler() : base(Mapper) { }

    protected override TextureVlcView CreatePlatformView() => new(Context);

    protected override void ConnectHandler(TextureVlcView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.MediaPlayer = VirtualView.MediaPlayer;
    }

    protected override void DisconnectHandler(TextureVlcView platformView)
    {
        platformView.MediaPlayer = null;
        base.DisconnectHandler(platformView);
    }

    private static void MapMediaPlayer(TextureVideoViewHandler handler, LibVLCSharp.MAUI.VideoView view) =>
        handler.PlatformView.MediaPlayer = view.MediaPlayer;
}

public sealed class TextureVlcView : TextureView, AWindow.ISurfaceCallback, IVLCVout.ICallback
{
    private MediaPlayer? _mediaPlayer;
    private AWindow? _window;

    public TextureVlcView(Context context) : base(context) { }

    public MediaPlayer? MediaPlayer
    {
        get => _mediaPlayer;
        set
        {
            if (ReferenceEquals(_mediaPlayer, value)) return;
            Detach();
            _mediaPlayer = value;
            if (_mediaPlayer is not null) Attach();
        }
    }

    protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
    {
        base.OnLayout(changed, left, top, right, bottom);
        if (right > left && bottom > top) _window?.SetWindowSize(right - left, bottom - top);
    }

    private void Attach()
    {
        _window = new AWindow(this);
        _window.AddCallback(this);
        _window.SetVideoView(this);
        _window.AttachViews();
        _mediaPlayer!.SetAndroidContext(_window.Handle);
        if (Width > 0 && Height > 0) _window.SetWindowSize(Width, Height);
    }

    private void Detach()
    {
        if (_window is null) return;
        try { _window.RemoveCallback(this); } catch { }
        try { _window.DetachViews(); } catch { }
        _window.Dispose();
        _window = null;
    }

    public void OnSurfacesCreated(IVLCVout? vout) => RequestLayout();
    public void OnSurfacesDestroyed(IVLCVout? vout) { }
    void AWindow.ISurfaceCallback.OnSurfacesCreated(AWindow? window) => RequestLayout();
    void AWindow.ISurfaceCallback.OnSurfacesDestroyed(AWindow? window) { }

    protected override void Dispose(bool disposing)
    {
        Detach();
        base.Dispose(disposing);
    }
}
