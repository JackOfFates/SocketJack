# LlmRuntime Regression Suite

Run these from the repository root:

```powershell
dotnet test .\LlmRuntime.Tests\LlmRuntime.Tests.csproj --no-restore
dotnet build .\LlmRuntime.Wpf\LlmRuntime.Wpf.csproj --no-restore
dotnet build .\JackLLM\JackLLM.csproj --no-restore
dotnet build .\SocketJack.sln --no-restore
```

Covered now:

- GGUF metadata parsing.
- Model registry scanning and download helpers.
- Runtime endpoint compatibility.
- GPU backend selection errors and load config echo.
- Tool definition persistence, safety, invocation, secrets, and audit.
- Agent session, file, terminal, and workflow primitives.
- GitHub workflow fallbacks and local git paths.
- JackLLM provider routing.
- Production readiness endpoints.

Known repo-wide failures should be tracked in `ISSUES.md` and fixed without blocking focused LlmRuntime validation.
