using JackLLM.Security;
using JackLLM.SecurityBroker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;

if (args.Any(arg => string.Equals(arg, "--development", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Decrypted development security mode is prohibited.");
bool localRelease = args.Any(arg => string.Equals(arg, "--local-release", StringComparison.OrdinalIgnoreCase));
int? parentProcessId = ReadIntegerArgument(args, "--parent-pid");
if (localRelease && !Environment.UserInteractive)
    throw new InvalidOperationException("Local Release broker mode cannot run as a Windows service.");

const string brokerMutexName = @"Global\SocketJack.JackLLM.SecurityBroker.Official.v1";
Mutex? brokerMutex;
try {
    brokerMutex = new Mutex(initiallyOwned: true, brokerMutexName, out bool createdNew);
    if (!createdNew) {
        brokerMutex.Dispose();
        return;
    }
} catch (UnauthorizedAccessException) {
    // An elevated broker or the Windows service already owns the machine-wide
    // broker mutex. A second, lower-privileged broker must not be allowed to run.
    return;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new BrokerMode(Development: false, AllowHashStampedLocalRelease: localRelease));
builder.Services.AddSingleton(new SecurityStateStore(development: false));
builder.Services.AddSingleton(provider =>
    new SecurityEngine(provider.GetRequiredService<SecurityStateStore>(), development: false));
builder.Services.AddSingleton<BuildIntegrityVerifier>();
builder.Services.AddHostedService<SecurityBrokerWorker>();
builder.Services.AddWindowsService(options => options.ServiceName = "JackLLM Security Broker");
try {
    using IHost host = builder.Build();
    using var parentLifetime = new CancellationTokenSource();
    if (parentProcessId is int parentId) {
        _ = Task.Run(async () => {
            try {
                using Process parent = Process.GetProcessById(parentId);
                await parent.WaitForExitAsync(parentLifetime.Token);
            } catch (OperationCanceledException) {
                return;
            } catch {
                // A missing or inaccessible parent is treated as already exited.
            }
            try { parentLifetime.Cancel(); } catch { }
        });
    }
    await host.RunAsync(parentLifetime.Token);
} finally {
    brokerMutex.ReleaseMutex();
    brokerMutex.Dispose();
}

static int? ReadIntegerArgument(string[] arguments, string name) {
    for (int index = 0; index + 1 < arguments.Length; index++) {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(arguments[index + 1], out int value) && value > 0)
            return value;
    }
    return null;
}
