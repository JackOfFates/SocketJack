using JackLLM.Security;
using JackLLM.SecurityBroker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

bool development = args.Any(arg => string.Equals(arg, "--development", StringComparison.OrdinalIgnoreCase));
if (development && !Environment.UserInteractive)
    throw new InvalidOperationException("The development broker cannot run as a Windows service.");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new BrokerMode(development));
builder.Services.AddSingleton(new SecurityStateStore(development));
builder.Services.AddSingleton(provider =>
    new SecurityEngine(provider.GetRequiredService<SecurityStateStore>(), development));
builder.Services.AddSingleton<BuildIntegrityVerifier>();
builder.Services.AddHostedService<SecurityBrokerWorker>();
builder.Services.AddWindowsService(options => options.ServiceName = "JackLLM Security Broker");
await builder.Build().RunAsync();
