using System.Net;
using System.Text.Json;
using AtmCash.Application.Outbox;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Common;
using AtmCash.Infrastructure.Persistence;
using AtmCash.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class OutboxTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Low_cash_alert_is_written_to_the_outbox_and_published_by_the_worker()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 12), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 20));

        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 300)).ReadAsync<QuoteDto>();

        var message = await AlertForAsync(atm.Id);
        Assert.NotNull(message);
        using var payload = JsonDocument.Parse(message.Payload);
        var alert = payload.RootElement;
        Assert.Equal(1, alert.GetProperty("slot").GetInt32());
        Assert.Equal("TRY", alert.GetProperty("currency").GetString());
        Assert.Equal(9, alert.GetProperty("availableCount").GetInt32());
        Assert.Equal("RESERVATION", alert.GetProperty("trigger").GetString());
        Assert.Equal(quote.Id, alert.GetProperty("correlationId").GetGuid());

        // The hosted worker polls every 2 seconds.
        var processedAt = await WaitForProcessedAsync(message.Id, TimeSpan.FromSeconds(20));
        Assert.NotNull(processedAt);
    }

    [Fact]
    public async Task No_alert_while_available_notes_stay_at_the_threshold()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 12), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 20));

        await (await _client.QuoteAsync(atm.Id, "TRY", 200)).ReadAsync<QuoteDto>();

        Assert.Null(await AlertForAsync(atm.Id));
    }

    [Fact]
    public async Task Failed_operation_leaves_no_alert_behind()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 12), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 20));
        var card = AtmScenario.NewCard();

        // Over the daily limit: rejected before anything is reserved or written.
        var response = await _client.QuoteAsync(atm.Id, "TRY", 25_000, card);

        await response.AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.DailyLimitExceeded);
        Assert.Null(await AlertForAsync(atm.Id));
    }

    private async Task<OutboxMessage?> AlertForAsync(Guid atmId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
        var alerts = await db.OutboxMessages.AsNoTracking().Where(m => m.Type == nameof(LowCashAlert)).ToListAsync();
        return alerts.SingleOrDefault(m => m.Payload.Contains(atmId.ToString(), StringComparison.Ordinal));
    }

    private async Task<DateTimeOffset?> WaitForProcessedAsync(Guid messageId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
            var processedAt = await db.OutboxMessages.Where(m => m.Id == messageId).Select(m => m.ProcessedAt).SingleAsync();
            if (processedAt is not null)
            {
                return processedAt;
            }

            await Task.Delay(250);
        }

        return null;
    }
}
