# heirowStocks

heirowStocks is the market watcher built into the heirowLLM Workstation. The
Workstation serves the dashboard at `/Stocks` and handles its typed WebSocket
protocol at `/Stocks/ws` on the existing Web Chat listener. It does not launch
or require a second executable.

The dashboard starts with NVDA and AMD, supports persistent watchlist editing,
charts, Today's Movers, Sustained Momentum, account selection, and Public API
credential settings. The Public secret is protected with Windows DPAPI before
being written to SocketJack DataServer storage with payload encryption enabled.
The saved secret and short-lived provider tokens are never returned to the
browser.
