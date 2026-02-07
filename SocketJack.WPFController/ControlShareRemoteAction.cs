using System;

namespace SocketJack.WPFController;
public sealed class ControlShareRemoteAction {
    public string ControlId { get; set; }

    public RemoteAction.ActionType Action { get; set; }

    public string Arguments { get; set; }

    public int Duration { get; set; } = 50;
}
