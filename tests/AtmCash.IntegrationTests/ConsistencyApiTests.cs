using System.Net;
using AtmCash.Application.Consistency;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Infrastructure.Persistence;
using AtmCash.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class ConsistencyApiTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Balances_match_the_ledger_after_every_kind_of_operation()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 50), ("TRY 50", 20), ("TRY 20", 10), ("USD 100", 5));
        var confirmed = await (await _client.QuoteAsync(atm.Id, "TRY", 370)).ReadAsync<QuoteDto>();
        await (await _client.ConfirmAsync(confirmed.Id)).ReadAsync<WithdrawalResultDto>();
        await _client.PostJsonAsync($"/api/atms/{atm.Id}/deposits", new
        {
            notes = new[] { new { currency = "TRY", denomination = 100, count = 3 }, new { currency = "EUR", denomination = 50, count = 2 } },
            unrecognisedCount = 1,
        });
        // Left open, so its notes are reserved.
        await (await _client.QuoteAsync(atm.Id, "USD", 200)).ReadAsync<QuoteDto>();

        var report = await (await _client.GetAsync($"/api/atms/{atm.Id}/consistency")).ReadAsync<ConsistencyReportDto>();

        var result = Assert.Single(report.Atms);
        Assert.True(result.IsConsistent);
        Assert.Equal(0, result.DifferenceCount);
        var usd = Assert.Single(result.Cassettes.Single(c => c.Slot == 4).Holdings);
        Assert.Equal((5, 5, 2, 2), (usd.StoredCount, usd.LedgerCount, usd.StoredReserved, usd.LedgerReserved));
        Assert.Contains(result.Cassettes.Single(c => c.Slot == 5).Holdings, h => h.Currency == Currency.EUR && h.LedgerCount == 2);
    }

    [Fact]
    public async Task A_balance_changed_outside_the_ledger_is_reported()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 50), ("TRY 50", 20), ("TRY 20", 10), ("USD 100", 5));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // Simulate a defect: a stored balance changed without a movement.
            var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
            var cassetteId = await db.Atms.Where(a => a.Id == atm.Id).SelectMany(a => a.Cassettes).Where(c => c.Slot == 2).Select(c => c.Id).SingleAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE cassette_holdings SET count = count - 3 WHERE cassette_id = {cassetteId}");
        }

        var single = await (await _client.GetAsync($"/api/atms/{atm.Id}/consistency")).ReadAsync<ConsistencyReportDto>();
        var all = await (await _client.GetAsync("/api/consistency")).ReadAsync<ConsistencyReportDto>();

        var result = Assert.Single(single.Atms);
        Assert.False(single.IsConsistent);
        Assert.Equal(1, result.DifferenceCount);
        var cassette = Assert.Single(result.Cassettes, c => !c.IsConsistent);
        var holding = Assert.Single(cassette.Holdings);
        Assert.Equal((2, 17, 20, -3), (cassette.Slot, holding.StoredCount, holding.LedgerCount, holding.CountDifference));
        Assert.All(result.Cassettes.Where(c => c.Slot != 2), c => Assert.True(c.IsConsistent));

        Assert.Contains(all.Atms, a => a.AtmId == atm.Id && !a.IsConsistent);
        Assert.True(all.InconsistentAtmCount >= 1);
    }

    [Fact]
    public async Task Unknown_atm_returns_not_found()
    {
        var response = await _client.GetAsync($"/api/atms/{Guid.NewGuid()}/consistency");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, ErrorCodes.AtmNotFound);
    }
}
