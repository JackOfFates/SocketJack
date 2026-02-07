using System;

namespace SocketJack.WPFController;
public sealed class ControlShareFrame {
    public string ControlId { get; set; }
    public ElementRoute Route { get; set; }

    // Content of the frame. JPEG-encoded bytes.
    public byte[] JpegBytes { get; set; }

    public int Quality { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public int UnixMs { get; set; }
}
