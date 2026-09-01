using Microsoft.Extensions.Options;
using SquashAgent.Configuration;
using SquashAgent.Connection;
using SquashAgent.Enrollment;
using SquashAgent.Execution;
using SquashAgent.Identity;
using SquashAgent.Service;
using SquashAgent.Storage;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("SquashAgent"));
builder.Services.AddWindowsService(options => options.ServiceName = "Squash Agent");

builder.Services.AddHttpClient<EnrollmentClient>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentOptions>>().Value);
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<AgentOptions>();
    return new DeviceIdentityStore(options.DataDirectory);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<AgentOptions>();
    return new ExecutionStore(options.DataDirectory);
});
builder.Services.AddSingleton<PowerShellExecutor>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<AgentOptions>();
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(EnrollmentClient));
    return new EnrollmentClient(http, options.ControlPlaneBaseUrl);
});
builder.Services.AddSingleton<AgentConnection>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
