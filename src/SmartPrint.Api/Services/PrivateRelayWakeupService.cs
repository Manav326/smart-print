using System.Collections.Concurrent;
using System.Threading.Channels;
using Npgsql;

namespace SmartPrint.Api.Services;

/// <summary>
/// Wakes only the paired Agent that owns a newly accepted private relay.
/// PostgreSQL notifications are hints, not the source of truth: the Agent always
/// re-reads the durable ticket table after waking, and long-poll timeout is the
/// recovery path when a notification listener is restarting.
/// </summary>
public sealed class PrivateRelayWakeupService(
    string connectionString,
    ILogger<PrivateRelayWakeupService> logger) : BackgroundService
{
    private const string NotificationChannel = "smartprint_private_relay";
    private readonly ConcurrentDictionary<Guid, Channel<bool>> waiters = new();

    public ChannelReader<bool> Subscribe(Guid agentDeviceId)
    {
        if (agentDeviceId == Guid.Empty)
            throw new ArgumentException("An Agent identity is required.", nameof(agentDeviceId));

        return waiters.GetOrAdd(agentDeviceId, static _ =>
            Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            })).Reader;
    }

    public async Task WaitForSignalAsync(
        ChannelReader<bool> reader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Normal long-poll timeout. The caller performs a final durable DB read.
        }
    }

    /// <summary>
    /// Publishes after the upload-ticket transaction commits. Failure is deliberately
    /// non-fatal: the Agent's bounded long-poll timeout will still discover the ticket.
    /// </summary>
    public async Task NotifyAgentAsync(Guid agentDeviceId)
    {
        if (agentDeviceId == Guid.Empty) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                "SELECT pg_notify('smartprint_private_relay', @agent_id)", connection);
            command.Parameters.AddWithValue("agent_id", agentDeviceId.ToString("N"));
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not publish the private-relay wake-up for Agent {AgentDeviceId}; its long-poll timeout remains the recovery path.",
                agentDeviceId);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception,
                "Private-relay wake-up for Agent {AgentDeviceId} was interrupted; its long-poll timeout remains the recovery path.",
                agentDeviceId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, notification) =>
                {
                    if (Guid.TryParseExact(notification.Payload, "N", out var agentDeviceId) &&
                        waiters.TryGetValue(agentDeviceId, out var channel))
                    {
                        channel.Writer.TryWrite(true);
                    }
                };

                await using (var listen = new NpgsqlCommand($"LISTEN {NotificationChannel}", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);

                logger.LogInformation("Private-relay PostgreSQL wake-up listener is active.");
                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Private-relay wake-up listener disconnected; long-poll timeouts will cover the restart.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
