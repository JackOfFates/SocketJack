using JackLLM.Mobile.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class RemoteViewportMathTests
{
    [TestMethod]
    public void CenterMapsToDesktopCenterAtEveryZoom()
    {
        foreach (double zoom in new[] { 1d, 2d, 4d })
        {
            RemoteNormalizedPoint point = RemoteViewportMath.MapToDesktop(500, 300, 1000, 600, 1920, 1080, zoom, 0, 0);
            Assert.AreEqual(0.5, point.X, 0.001);
            Assert.AreEqual(0.5, point.Y, 0.001);
        }
    }

    [TestMethod]
    public void AspectFitBlackBarsClampToDesktopEdges()
    {
        RemoteNormalizedPoint left = RemoteViewportMath.MapToDesktop(0, 300, 1000, 600, 1920, 1080, 1, 0, 0);
        RemoteNormalizedPoint right = RemoteViewportMath.MapToDesktop(1000, 300, 1000, 600, 1920, 1080, 1, 0, 0);
        Assert.AreEqual(0, left.X, 0.001);
        Assert.AreEqual(1, right.X, 0.001);
    }

    [TestMethod]
    public void CursorAndTouchMappingsRoundTripWithPanAndZoom()
    {
        RemoteViewportPoint cursor = RemoteViewportMath.MapCursor(0.72, 0.31, 1080, 1500, 1920, 1080, 3, -210, 330);
        RemoteNormalizedPoint mapped = RemoteViewportMath.MapToDesktop(cursor.X, cursor.Y, 1080, 1500, 1920, 1080, 3, -210, 330);
        Assert.AreEqual(0.72, mapped.X, 0.001);
        Assert.AreEqual(0.31, mapped.Y, 0.001);
    }

    [TestMethod]
    public void PanIsClampedToScaledViewport()
    {
        RemoteViewportPoint point = RemoteViewportMath.ClampPan(9000, -9000, 1000, 600, 2);
        Assert.AreEqual(500, point.X, 0.001);
        Assert.AreEqual(-300, point.Y, 0.001);
    }

    [TestMethod]
    public void NativeCropZoomsAroundDesktopCenter()
    {
        RemoteCropRect crop = RemoteViewportMath.Crop(1080, 1500, 1920, 1080, 2, 0, 0);
        Assert.AreEqual(480, crop.X, 0.001);
        Assert.AreEqual(270, crop.Y, 0.001);
        Assert.AreEqual(960, crop.Width, 0.001);
        Assert.AreEqual(540, crop.Height, 0.001);
    }

    [TestMethod]
    public void NativeCropPanReachesDesktopEdge()
    {
        RemoteViewportPoint pan = RemoteViewportMath.ClampPan(9000, 0, 1080, 1500, 1920, 1080, 2);
        RemoteCropRect crop = RemoteViewportMath.Crop(1080, 1500, 1920, 1080, 2, pan.X, pan.Y);
        Assert.AreEqual(0, crop.X, 0.001);
        Assert.AreEqual(960, crop.Width, 0.001);
    }
}
