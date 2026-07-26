using JackLLM.Security;
using JackLLM.SecurityBroker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Any(arg => string.Equals(arg, "--development", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Decrypted development security mode is prohibited.");
bool localRelease = args.Any(arg => string.Equals(arg, "--local-release", StringComparison.OrdinalIgnoreCase));
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
    await builder.Build().RunAsync();
} finally {
    brokerMutex.ReleaseMutex();
    brokerMutex.Dispose();
}
