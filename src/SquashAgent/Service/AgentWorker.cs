using Microsoft.Extensions.Options;
using SquashAgent.Configuration;
using SquashAgent.Connection;
using SquashAgent.Enrollment;
using SquashAgent.Identity;
using SquashAgent.Execution;
using SquashAgent.Storage;

namespace SquashAgent.Service;

public sealed class AgentWorker : BackgroundService
{
    private readonly AgentOptions _options;
    private readonly DeviceIdentityStore _identityStore;
    private readonly EnrollmentClient _enrollment;
    private readonly ExecutionStore _executionStore;
    private readonly AgentConnection _connection;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        IOptions<AgentOptions> options,
        DeviceIdentityStore identityStore,
        EnrollmentClient enrollment,
        ExecutionStore executionStore,
        AgentConnection connection,
        ILogger<AgentWorker> logger)
    {
        _options = options.Value;
        _identityStore = identityStore;
        _enrollment = enrollment;
        _executionStore = executionStore;
        _connection = connection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await _executionStore.InitializeAsync(stoppingToken);

    var identity = await _identityStore.LoadAsync(stoppingToken);

    if (identity is null)
    {
        var deviceId = DeviceIdentityStore.GetStableDeviceId();

        _logger.LogInformation(
            "Enrolling device {DeviceId}",
            deviceId);

        identity = await _enrollment.EnrollAsync(
            _options.BootstrapToken,
            deviceId,
            stoppingToken);

        await _identityStore.SaveAsync(
            identity,
            stoppingToken);

        _logger.LogInformation(
            "Enrollment complete for device {DeviceId}",
            identity.DeviceId);
    }

    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            await _connection.RunAsync(identity, stoppingToken);
        }
        catch (AgentReenrollmentRequiredException)
        {
            _logger.LogWarning(
                "Control Plane rejected device {DeviceId}; re-enrolling",
                identity.DeviceId);

            identity = await _enrollment.EnrollAsync(
                _options.BootstrapToken,
                identity.DeviceId,
                stoppingToken);

            await _identityStore.SaveAsync(
                identity,
                stoppingToken);

            _logger.LogInformation(
                "Re-enrollment complete for device {DeviceId}",
                identity.DeviceId);

            await Task.Delay(
                TimeSpan.FromSeconds(1),
                stoppingToken);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Agent connection failed; retrying in 30s");

            await Task.Delay(
                TimeSpan.FromSeconds(30),
                stoppingToken);
        }
    }
}
}
