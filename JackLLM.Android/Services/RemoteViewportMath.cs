namespace JackLLM.Mobile.Services;

public static class RemoteViewportMath
{
    public static RemoteNormalizedPoint MapToDesktop(
        double viewX, double viewY, double viewportWidth, double viewportHeight,
        double desktopWidth, double desktopHeight, double zoom, double panX, double panY)
    {
        viewportWidth = Math.Max(1, viewportWidth);
        viewportHeight = Math.Max(1, viewportHeight);
        zoom = Math.Clamp(zoom, 1, 4);
        RemoteContentRect content = ContentRect(viewportWidth, viewportHeight, desktopWidth, desktopHeight);
        double localX = (viewX - viewportWidth / 2d - panX) / zoom + viewportWidth / 2d;
        double localY = (viewY - viewportHeight / 2d - panY) / zoom + viewportHeight / 2d;
        return new RemoteNormalizedPoint(
            Math.Clamp((localX - content.X) / content.Width, 0, 1),
            Math.Clamp((localY - content.Y) / content.Height, 0, 1));
    }

    public static RemoteViewportPoint MapCursor(
        double normalizedX, double normalizedY, double viewportWidth, double viewportHeight,
        double desktopWidth, double desktopHeight, double zoom, double panX, double panY)
    {
        viewportWidth = Math.Max(1, viewportWidth);
        viewportHeight = Math.Max(1, viewportHeight);
        RemoteContentRect content = ContentRect(viewportWidth, viewportHeight, desktopWidth, desktopHeight);
        double baseX = content.X + Math.Clamp(normalizedX, 0, 1) * content.Width;
        double baseY = content.Y + Math.Clamp(normalizedY, 0, 1) * content.Height;
        return new RemoteViewportPoint(
            (baseX - viewportWidth / 2d) * zoom + viewportWidth / 2d + panX,
            (baseY - viewportHeight / 2d) * zoom + viewportHeight / 2d + panY);
    }

    public static RemoteViewportPoint ClampPan(double panX, double panY, double viewportWidth, double viewportHeight, double zoom)
    {
        double maxX = Math.Max(0, zoom - 1d) * Math.Max(1, viewportWidth) / 2d;
        double maxY = Math.Max(0, zoom - 1d) * Math.Max(1, viewportHeight) / 2d;
        return new RemoteViewportPoint(Math.Clamp(panX, -maxX, maxX), Math.Clamp(panY, -maxY, maxY));
    }

    public static RemoteViewportPoint ClampPan(
        double panX, double panY, double viewportWidth, double viewportHeight,
        double desktopWidth, double desktopHeight, double zoom)
    {
        RemoteContentRect content = ContentRect(
            Math.Max(1, viewportWidth), Math.Max(1, viewportHeight), desktopWidth, desktopHeight);
        double maxX = Math.Max(0, zoom - 1d) * content.Width / 2d;
        double maxY = Math.Max(0, zoom - 1d) * content.Height / 2d;
        return new RemoteViewportPoint(Math.Clamp(panX, -maxX, maxX), Math.Clamp(panY, -maxY, maxY));
    }

    public static RemoteCropRect Crop(
        double viewportWidth, double viewportHeight, double desktopWidth, double desktopHeight,
        double zoom, double panX, double panY)
    {
        desktopWidth = Math.Max(2, desktopWidth);
        desktopHeight = Math.Max(2, desktopHeight);
        zoom = Math.Clamp(zoom, 1, 4);
        RemoteContentRect content = ContentRect(
            Math.Max(1, viewportWidth), Math.Max(1, viewportHeight), desktopWidth, desktopHeight);
        double width = desktopWidth / zoom;
        double height = desktopHeight / zoom;
        double centerX = .5d - panX / (content.Width * zoom);
        double centerY = .5d - panY / (content.Height * zoom);
        double x = Math.Clamp(centerX * desktopWidth - width / 2d, 0, desktopWidth - width);
        double y = Math.Clamp(centerY * desktopHeight - height / 2d, 0, desktopHeight - height);
        return new RemoteCropRect(x, y, width, height);
    }

    private static RemoteContentRect ContentRect(double width, double height, double desktopWidth, double desktopHeight)
    {
        desktopWidth = Math.Max(1, desktopWidth);
        desktopHeight = Math.Max(1, desktopHeight);
        double scale = Math.Min(width / desktopWidth, height / desktopHeight);
        double fittedWidth = desktopWidth * scale;
        double fittedHeight = desktopHeight * scale;
        return new RemoteContentRect((width - fittedWidth) / 2d, (height - fittedHeight) / 2d, fittedWidth, fittedHeight);
    }
}

public readonly record struct RemoteNormalizedPoint(double X, double Y);
public readonly record struct RemoteViewportPoint(double X, double Y);
public readonly record struct RemoteCropRect(double X, double Y, double Width, double Height);
internal readonly record struct RemoteContentRect(double X, double Y, double Width, double Height);
