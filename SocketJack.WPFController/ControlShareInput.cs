using System;

namespace SocketJack.WPFController;
public sealed class ControlShareInput {
    public string ControlId { get; set; }

    // Normalized coordinates (0..1) relative to the shared frame.
    public double X { get; set; }
    public double Y { get; set; }

    public bool IsMove { get; set; }
    public bool IsClick { get; set; }

    public int Button { get; set; }
}
