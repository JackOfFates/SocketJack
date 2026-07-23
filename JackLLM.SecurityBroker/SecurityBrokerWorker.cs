using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using JackLLM.Security;

namespace JackLLM.SecurityBroker;

public sealed record BrokerMode(bool Development);

public sealed class SecurityBrokerWorker : BackgroundService {
    private readonly SecurityEngine _engine;
    private readonly BuildIntegrityVerifier _integrity;
    private readonly BrokerMode _mode;
    private readonly ILogger<SecurityBrokerWorker> _logger;

    public SecurityBrokerWorker(SecurityEngine engine, BuildIntegrityVerifier integrity, BrokerMode mode, ILogger<SecurityBrokerWorker> logger) {
        _engine = engine;
        _integrity = integrity;
        _mode = mode;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        string pipeName = _mode.Development ? SecurityProtocol.DevelopmentPipeName : SecurityProtocol.OfficialPipeName;
        while (!stoppingToken.IsCancellationRequested) {
            try {
                using NamedPipeServerStream pipe = CreatePipe(pipeName);
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(pipe, stoppingToken);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                _logger.LogError(ex, "Security broker request failed");
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken) {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        SecurityResponse response;
        if (!TryGetClientProcessId(pipe, out uint clientProcessId)) {
            response = Failure(SecurityStateKind.IntegrityFailure, "Unable to identify the security client process.");
        } else {
            BuildIntegrityResult integrity = _integrity.Verify(clientProcessId, _mode.Development);
            if (!integrity.Success) {
                response = Failure(SecurityStateKind.IntegrityFailure, integrity.Message);
            } else {
                string? line = await reader.ReadLineAsync(cancellationToken);
                bool clientIsAdministrator = IsClientAdministrator(pipe);
                response = await ProcessAsync(line, clientIsAdministrator);
            }
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, SecurityProtocol.Json));
    }

    private Task<SecurityResponse> ProcessAsync(string? line, bool clientIsAdministrator) {
        SecurityRequest? request;
        try { request = JsonSerializer.Deserialize<SecurityRequest>(line ?? "", SecurityProtocol.Json); }
        catch { request = null; }
        if (request == null || request.Version != SecurityProtocol.Version)
            return Task.FromResult(Failure(SecurityStateKind.Error, "Unsupported security protocol request."));
        if (request.Operation is SecurityOperation.Recover or SecurityOperation.RebindHardware && !clientIsAdministrator)
            return Task.FromResult(Failure(SecurityStateKind.IntegrityFailure, "Recovery and hardware rebinding require an elevated JackLLM process."));
        SecurityResponse response = request.Operation switch {
            SecurityOperation.Status => _engine.GetStatus(),
            SecurityOperation.BeginEnroll => _engine.Begin(SecurityOperation.BeginEnroll),
            SecurityOperation.Enroll => _engine.Enroll(request),
            SecurityOperation.BeginUnlock => _engine.Begin(SecurityOperation.BeginUnlock),
            SecurityOperation.CompleteUnlock when !string.IsNullOrWhiteSpace(request.UnlockGrant) =>
                _engine.ConsumeGrant(request.UnlockGrant)
                    ? new SecurityResponse { Success = true, State = SecurityStateKind.Unlocked, Message = "Unlock grant accepted.", DevelopmentMode = _mode.Development }
                    : Failure(SecurityStateKind.Error, "The unlock grant expired or was already used."),
            SecurityOperation.CompleteUnlock => _engine.Unlock(request),
            SecurityOperation.ChangePassword => _engine.ChangePassword(request),
            SecurityOperation.Recover => _engine.Recover(request),
            SecurityOperation.RebindHardware => _engine.RebindHardware(request),
            _ => Failure(SecurityStateKind.Error, "Unsupported security operation.")
        };
        return Task.FromResult(response);
    }

    private static NamedPipeServerStream CreatePipe(string pipeName) {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, 4096, 4096, security);
    }

    private static SecurityResponse Failure(SecurityStateKind state, string message) => new() { State = state, Message = message };

    private static bool IsClientAdministrator(NamedPipeServerStream pipe) {
        bool administrator = false;
        try {
            pipe.RunAsClient(() => {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        } catch { }
        return administrator;
    }

    private static bool TryGetClientProcessId(NamedPipeServerStream pipe, out uint processId) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
