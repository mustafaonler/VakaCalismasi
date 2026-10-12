using System.Net;
using AtmCash.Application.Reports;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.IntegrationTests.Infrastructure;

namespace AtmCash.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class ReportApiTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Report_shows_note_reserved_and_available_counts_with_currency_and_try_totals()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 50), ("TRY 50", 20), ("TRY 20", 8), ("USD 100", 5));
        await _client.PostJsonAsync($"/api/atms/{atm.Id}/deposits", new
        {
            notes = new[] { new { currency = "EUR", denomination = 50, count = 2 } },
            unrecognisedCount = 0,
        });
        // Open quotes: TRY 100 x3 and USD 100 x2 stay reserved.
        await (await _client.QuoteAsync(atm.Id, "TRY", 300)).ReadAsync<QuoteDto>();
        await (await _client.QuoteAsync(atm.Id, "USD", 200)).ReadAsync<QuoteDto>();

        var report = await (await _client.GetAsync($"/api/reports/operations?atmId={atm.Id}")).ReadAsync<OperationsReportDto>();

        var result = Assert.Single(report.Atms);
        Assert.Equal(
            [(1, 50, 3, 47, false), (2, 20, 0, 20, false), (3, 8, 0, 8, true), (4, 5, 2, 3, true), (5, 2, 0, 2, false)],
            result.Cassettes.Select(c => (c.Slot, c.NoteCount, c.ReservedCount, c.AvailableCount, c.IsLowCash)));
        var depositNote = Assert.Single(result.Cassettes[4].Notes);
        Assert.Equal((Currency.EUR, 50, 2), (depositNote.Currency, depositNote.Denomination, depositNote.NoteCount));

        var rates = report.ExchangeRates.ToDictionary(r => r.Currency, r => r.RateToTry);
        var tryTotal = Assert.Single(result.Totals, t => t.Currency == Currency.TRY);
        Assert.Equal((78, 3, 75, 6_160m, 5_860m, 6_160m), (tryTotal.NoteCount, tryTotal.ReservedCount, tryTotal.AvailableCount, tryTotal.Amount, tryTotal.AvailableAmount, tryTotal.AmountTry));
        var usdTotal = Assert.Single(result.Totals, t => t.Currency == Currency.USD);
        Assert.Equal((500m, 300m, 500m * rates[Currency.USD]), (usdTotal.Amount, usdTotal.AvailableAmount, usdTotal.AmountTry));
        var eurTotal = Assert.Single(result.Totals, t => t.Currency == Currency.EUR);
        Assert.Equal(100m * rates[Currency.EUR], eurTotal.AmountTry);

        Assert.Equal(6_160m + 500m * rates[Currency.USD] + 100m * rates[Currency.EUR], result.TotalTry);
        Assert.Equal(result.TotalTry, report.TotalTry);
        Assert.Equal([Currency.TRY, Currency.USD, Currency.EUR], result.Totals.Select(t => t.Currency));
    }

    [Fact]
    public async Task Network_report_covers_every_atm_and_sums_their_totals()
    {
        var first = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0));
        var second = await _client.InServiceAtmAsync(("TRY 100", 20), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0));

        var report = await (await _client.GetAsync("/api/reports/operations")).ReadAsync<OperationsReportDto>();

        Assert.Contains(report.Atms, a => a.AtmId == first.Id && a.TotalTry == 1_000m);
        Assert.Contains(report.Atms, a => a.AtmId == second.Id && a.TotalTry == 2_000m);
        Assert.Equal(report.Atms.Sum(a => a.TotalTry), report.TotalTry);
        Assert.Equal(
            report.Atms.SelectMany(a => a.Totals).Where(t => t.Currency == Currency.TRY).Sum(t => t.Amount),
            report.Totals.Single(t => t.Currency == Currency.TRY).Amount);
    }

    [Fact]
    public async Task Report_amounts_have_two_decimals_and_statuses_are_included()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 1));

        var json = await (await _client.GetAsync($"/api/reports/operations?atmId={atm.Id}")).Content.ReadAsStringAsync();

        Assert.Contains("\"amount\":1000.00", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"IN_SERVICE\"", json, StringComparison.Ordinal);
        Assert.Matches("\"totalTry\":\\d+\\.\\d{2}[,}]", json);
        Assert.Equal(AtmStatus.InService, (await (await _client.GetAsync($"/api/reports/operations?atmId={atm.Id}")).ReadAsync<OperationsReportDto>()).Atms[0].Status);
    }

    [Fact]
    public async Task Unknown_atm_returns_not_found()
    {
        var response = await _client.GetAsync($"/api/reports/operations?atmId={Guid.NewGuid()}");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, ErrorCodes.AtmNotFound);
    }
}
