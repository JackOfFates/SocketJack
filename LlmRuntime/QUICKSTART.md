# LlmRuntime Quickstart

## Start The Runtime

```csharp
using LlmRuntime;

using var runtime = new LlmRuntimeHost(new LlmRuntimeOptions
{
    Port = 1234,
    ModelRoot = Path.Combine(Environment.CurrentDirectory, "Models")
});

runtime.Start();
Console.WriteLine("LlmRuntime is listening on http://127.0.0.1:1234");
Console.ReadLine();
```

## Download And Load A Model

1. Open `JackLLM`.
2. Select `Embedded LlmRuntime` as the provider.
3. Open the `Model Browser` tab.
4. Pick a GGUF model that the card marks as fitting memory and disk.
5. Leave `Load after download` checked.

The model is saved under `cwd\Models`.

## Check The API

```powershell
Invoke-RestMethod http://127.0.0.1:1234/v1/models
Invoke-RestMethod http://127.0.0.1:1234/api/v1/production/onboarding
Invoke-RestMethod http://127.0.0.1:1234/api/v1/production/diagnostics
```

## Chat

```powershell
$body = @{
  model = "your-model-name"
  messages = @(@{ role = "user"; content = "Say hello from SocketJack." })
} | ConvertTo-Json -Depth 5

Invoke-RestMethod http://127.0.0.1:1234/v1/chat/completions -Method Post -Body $body -ContentType "application/json"
```

## Production Checks

Use these local-only endpoints before publishing or demoing:

- `GET /api/v1/production/onboarding`
- `GET /api/v1/production/diagnostics`
- `GET /api/v1/production/analytics/local`
- `GET /api/v1/production/accessibility`
- `GET /api/v1/production/installer`
- `GET /api/v1/production/regression-suite`
- `GET /api/v1/production/golden-path`
