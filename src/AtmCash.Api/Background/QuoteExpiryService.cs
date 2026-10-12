using AtmCash.Application.Withdrawals;

namespace AtmCash.Api.Background;

/// <summary>
/// Releases the reservations of quotes older than 60 seconds (B4). Quote and confirm requests
/// also expire overdue quotes of their ATM on the spot, so this sweep only bounds how long
/// an idle ATM keeps notes reserved.
/// </summary>
internal sealed partial class QuoteExpiryService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<QuoteExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var expired = await scope.ServiceProvider.GetRequiredService<WithdrawalService>()
                    .ExpireOverdueQuotesAsync(stoppingToken);
                if (expired > 0)
                {
                    LogExpired(logger, expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} withdrawal quote(s) and released their notes")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quote expiry sweep failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
