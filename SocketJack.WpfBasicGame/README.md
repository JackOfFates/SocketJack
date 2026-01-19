# Using SocketJack to make a basic game in WPF

This project is a minimal WPF demo that uses the base SocketJack TCP types (`TcpServer` / `TcpClient`) to run a tiny multiplayer "Click Race".

## How to run

1. Set `SocketJack.WpfBasicGame` as startup project.
2. Start the app **twice**.
3. In the first instance: select **Host** and click `Connect`.
4. In the second instance: select **Join** and click `Connect`.
5. Back in the host instance: click `Start Round`.
6. Both players click the moving `CLICK` target as fast as possible.

The host runs a `TcpServer` and broadcasts score updates. Each player uses a `TcpClient`.
