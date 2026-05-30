using System;

namespace SocketJack.WPF {
    namespace Controller {
        public sealed class ControlShareFrame {
            public string ControlId { get; set; }
            public ElementRoute Route { get; set; }

            // Content of a key frame. JPEG-encoded bytes.
            public byte[] JpegBytes { get; set; }

            // Content of a delta frame. JPEG-encoded bytes for the dirty rectangle only.
            public byte[] DeltaJpegBytes { get; set; }

            public bool IsDelta { get; set; }
            public int DirtyX { get; set; }
            public int DirtyY { get; set; }
            public int DirtyWidth { get; set; }
            public int DirtyHeight { get; set; }
            public int PixelWidth { get; set; }
            public int PixelHeight { get; set; }
            public double DpiX { get; set; }
            public double DpiY { get; set; }
            public int Sequence { get; set; }
            public int BaseSequence { get; set; }
            public double ChangedRatio { get; set; }

            public int Quality { get; set; }

            public int Width { get; set; }
            public int Height { get; set; }
            public int UnixMs { get; set; }
        }
    }
}
