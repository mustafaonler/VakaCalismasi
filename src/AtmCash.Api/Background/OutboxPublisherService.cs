using AtmCash.Application.Abstractions;

namespace AtmCash.Api.Background;

/// <summary>
/// Publishes outbox events (B10). Here "publishing" means writing a warning log; a real deployment
/// would hand the event to a message broker or notification service at the same point.
/// </summary>
/// <remarks>
/// Each batch is locked with SKIP LOCKED, logged and marked processed in one transaction. If the
/// process dies before the commit, the messages stay pending and are picked up again, so delivery
/// is at-least-once; consumers can deduplicate on the message id.
/// </remarks>
internal sealed partial class OutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Drain the backlog before waiting for the next tick.
                while (await PublishBatchAsync(stoppingToken) == BatchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAtmDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var messages = await db.LockPendingOutboxMessagesAsync(BatchSize, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var message in messages)
        {
            LogPublished(logger, message.Type, message.Id, message.Payload);
            message.MarkProcessed(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Type} {MessageId}: {Payload}")]
    private static partial void LogPublished(ILogger logger, string type, Guid messageId, string payload);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox publishing failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
