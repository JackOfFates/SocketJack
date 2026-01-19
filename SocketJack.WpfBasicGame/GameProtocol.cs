namespace SocketJack.WpfBasicGame;

// Game wire protocol messages.
// These types are whitelisted in SocketJack options on both client and server.
// Keep them simple (POCOs) so they serialize efficiently and are easy to version.
internal sealed class StartRoundMessage {
    // Total round duration from the host's perspective.
    public int RoundLengthMs { get; set; }

    // Host time used by clients for UI sync and latency-friendly scheduling.
    public long ServerUnixMs { get; set; }
}

// Authoritative target position sent from the server.
internal sealed class TargetStateMessage {
    // Target top-left in canvas coordinates.
    public int TargetX { get; set; }

    // Target top-left in canvas coordinates.
    public int TargetY { get; set; }
}

// Client click event reported in canvas coordinates.
// Server validates and awards points based on click proximity.
internal sealed class ClickMessage {
    public int ClickX { get; set; }

    public int ClickY { get; set; }
}

// Score update broadcast from the server.
// Clients use this to render a live scoreboard.
internal sealed class PointsUpdateMessage {
    private string playerId = "";

    // P2P identity string (Guid) for the player being updated.
    public string PlayerId { get => playerId; set => playerId = value; }

    public int Points { get; set; }
}

// Cursor state broadcast.
// Clients interpolate/smooth these updates for 60 FPS rendering.
internal sealed class CursorStateMessage {
    private string playerId = "";

    // Set by the server when rebroadcasting so peers can associate cursor data.
    public string PlayerId { get => playerId; set => playerId = value; }

    // Cursor position in canvas coordinates.
    public int X { get; set; }

    // Cursor position in canvas coordinates.
    public int Y { get; set; }
}
