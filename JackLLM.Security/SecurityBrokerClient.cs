using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace JackLLM.Security;

public sealed class SecurityBrokerClient {
    private readonly string _pipeName;
    public SecurityBrokerClient(bool development) => _pipeName = development ? SecurityProtocol.DevelopmentPipeName : SecurityProtocol.OfficialPipeName;

    public async Task<SecurityResponse> SendAsync(SecurityRequest request, CancellationToken cancellationToken = default, TimeSpan? timeoutValue = null) {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutValue ?? TimeSpan.FromSeconds(10));
        try {
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, SecurityProtocol.Json));
            string? line = await reader.ReadLineAsync(timeout.Token);
            return JsonSerializer.Deserialize<SecurityResponse>(line ?? "", SecurityProtocol.Json)
                ?? new SecurityResponse { State = SecurityStateKind.Error, Message = "Security broker returned an invalid response." };
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return new SecurityResponse { State = SecurityStateKind.Error, Message = "The JackLLM Security Broker did not respond." };
        } catch (Exception ex) {
            return new SecurityResponse { State = SecurityStateKind.Error, Message = "The JackLLM Security Broker is unavailable: " + ex.Message };
        }
    }
}
