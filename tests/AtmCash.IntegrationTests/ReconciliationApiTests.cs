using System.Net;
using AtmCash.Application.Atms;
using AtmCash.Application.Reconciliations;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Reconciliations;
using AtmCash.Infrastructure.Persistence;
using AtmCash.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class ReconciliationApiTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Large_difference_needs_a_second_user_before_the_atm_returns_to_service()
    {
        var atm = await AtmInMaintenanceAsync();

        // 15 x TRY 100 missing: TRY 1,500 > 1,000.
        var reconciliation = await (await ReconcileAsync(atm.Id, "ayse", 35, 20, 10, 5)).ReadAsync<ReconciliationDto>();

        Assert.Equal(ReconciliationStatus.AwaitingApproval, reconciliation.Status);
        Assert.Equal(1_500m, reconciliation.TotalDifferenceTry);
        var details = await _client.GetAtmAsync(atm.Id);
        Assert.Equal((AtmStatus.Maintenance, reconciliation.Id, 35), (details.Status, details.PendingReconciliationId, details.Cassettes[0].Count));

        await (await ChangeStatusAsync(atm.Id, "IN_SERVICE")).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ReconciliationAwaitingApproval);
        await (await ApproveAsync(reconciliation.Id, "AYSE")).AssertProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.SameUserApproval);

        var approved = await (await ApproveAsync(reconciliation.Id, "mehmet")).ReadAsync<ReconciliationDto>();

        Assert.Equal((ReconciliationStatus.Approved, "mehmet"), (approved.Status, approved.ApprovedBy));
        Assert.Null((await _client.GetAtmAsync(atm.Id)).PendingReconciliationId);
        Assert.Equal(AtmStatus.InService, (await (await ChangeStatusAsync(atm.Id, "IN_SERVICE")).ReadAsync<AtmDetailsDto>()).Status);
        await (await ApproveAsync(reconciliation.Id, "zeynep")).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ReconciliationNotAwaitingApproval);
    }

    [Fact]
    public async Task Small_difference_completes_and_is_booked_as_adjustment_movements()
    {
        var atm = await AtmInMaintenanceAsync();

        // 2 x TRY 20 extra, 1 x TRY 50 missing: TRY 90.
        var reconciliation = await (await ReconcileAsync(atm.Id, "ayse", 50, 19, 12, 5)).ReadAsync<ReconciliationDto>();

        Assert.Equal(ReconciliationStatus.Completed, reconciliation.Status);
        Assert.Equal(90m, reconciliation.TotalDifferenceTry);
        Assert.Equal(
            [(2, -1, -50m), (3, 2, 40m)],
            reconciliation.Lines.Where(l => l.Difference != 0).Select(l => (l.Slot, l.Difference, l.DifferenceTry)));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
        var adjustments = await db.Movements
            .Where(m => m.CorrelationId == reconciliation.Id)
            .Select(m => new { m.Type, m.Quantity })
            .ToListAsync();
        Assert.All(adjustments, m => Assert.Equal(MovementType.ReconciliationAdjustment, m.Type));
        Assert.Equal([-1, 2], adjustments.Select(m => m.Quantity).Order());

        Assert.Equal(AtmStatus.InService, (await (await ChangeStatusAsync(atm.Id, "IN_SERVICE")).ReadAsync<AtmDetailsDto>()).Status);
    }

    [Fact]
    public async Task Foreign_differences_are_valued_with_the_seeded_exchange_rate()
    {
        var rates = await (await _client.GetAsync("/api/exchange-rates")).ReadAsync<List<ExchangeRateDto>>();
        var usd = rates.Single(r => r.Currency == Currency.USD).RateToTry;
        var atm = await AtmInMaintenanceAsync();

        var reconciliation = await (await ReconcileAsync(atm.Id, "ayse", 50, 20, 10, 4)).ReadAsync<ReconciliationDto>();

        var line = Assert.Single(reconciliation.Lines, l => l.Difference != 0);
        Assert.Equal((Currency.USD, -100 * usd), (line.Currency, line.DifferenceTry));
        Assert.Equal(ReconciliationStatus.AwaitingApproval, reconciliation.Status);
        Assert.Equal([Currency.TRY, Currency.USD, Currency.EUR], rates.Select(r => r.Currency));
    }

    [Fact]
    public async Task Deposit_cassette_is_counted_per_currency_and_denomination()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0));
        await _client.PostJsonAsync($"/api/atms/{atm.Id}/deposits", new
        {
            notes = new[] { new { currency = "EUR", denomination = 20, count = 5 } },
            unrecognisedCount = 0,
        });
        await ChangeStatusAsync(atm.Id, "MAINTENANCE");

        var reconciliation = await (await ReconcileAsync(
                atm.Id, "ayse", 10, 0, 0, 0, new { currency = "EUR", denomination = 20, count = 4 }, new { currency = "TRY", denomination = 500, count = 1 }))
            .ReadAsync<ReconciliationDto>();

        Assert.Equal(
            [(Currency.TRY, 500, 0, 1), (Currency.EUR, 20, 5, 4)],
            reconciliation.Lines.Where(l => l.Slot == 5).Select(l => (l.Currency, l.Denomination, l.Expected, l.Counted)));
        var deposit = (await _client.GetAtmAsync(atm.Id)).Cassettes[4];
        Assert.Equal(5, deposit.Count);
        Assert.Equal(2, deposit.Notes.Count);
    }

    [Fact]
    public async Task Try_amounts_are_returned_with_two_decimals()
    {
        var atm = await AtmInMaintenanceAsync();

        // 1 x USD 100 missing: 100 x the seeded USD rate, computed with six-decimal rates.
        var response = await ReconcileAsync(atm.Id, "ayse", 50, 20, 10, 4);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Matches("\"totalDifferenceTry\":\\d+\\.\\d{2}[,}]", json);
        Assert.Contains("\"approvalThresholdTry\":1000.00", json, StringComparison.Ordinal);
        Assert.Matches("\"differenceTry\":-\\d+\\.\\d{2}[,}]", json);
    }

    [Fact]
    public async Task Reconciliation_is_rejected_outside_maintenance()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 50), ("TRY 50", 20), ("TRY 20", 10), ("USD 100", 5));

        var response = await ReconcileAsync(atm.Id, "ayse", 50, 20, 10, 5);

        await response.AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
    }

    [Fact]
    public async Task User_header_is_required()
    {
        var atm = await AtmInMaintenanceAsync();

        await (await ReconcileAsync(atm.Id, null, 50, 20, 10, 5)).AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.UserRequired);

        var pending = await (await ReconcileAsync(atm.Id, "ayse", 10, 20, 10, 5)).ReadAsync<ReconciliationDto>();
        await (await ApproveAsync(pending.Id, null)).AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.UserRequired);
    }

    [Fact]
    public async Task Invalid_counts_and_unknown_reconciliations_are_rejected()
    {
        var atm = await AtmInMaintenanceAsync();

        var missingDeposit = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/atms/{atm.Id}/reconciliations", "ayse",
            new { cassettes = new[] { new { slot = 1, count = 50 } } }));
        await missingDeposit.AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidReconciliationCount);
        await (await ApproveAsync(Guid.NewGuid(), "mehmet")).AssertProblemAsync(HttpStatusCode.NotFound, ErrorCodes.ReconciliationNotFound);
    }

    /// <summary>TRY100 x50, TRY50 x20, TRY20 x10, USD100 x5, empty deposit cassette; in MAINTENANCE.</summary>
    private async Task<AtmDetailsDto> AtmInMaintenanceAsync()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 50), ("TRY 50", 20), ("TRY 20", 10), ("USD 100", 5));
        return await (await ChangeStatusAsync(atm.Id, "MAINTENANCE")).ReadAsync<AtmDetailsDto>();
    }

    private Task<HttpResponseMessage> ChangeStatusAsync(Guid atmId, string status) =>
        _client.PostJsonAsync($"/api/atms/{atmId}/status", new { status });

    private Task<HttpResponseMessage> ReconcileAsync(
        Guid atmId, string? user, int try100, int try50, int try20, int usd100, params object[] depositNotes) =>
        _client.SendAsync(Request(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations", user, new
        {
            cassettes = new object[]
            {
                new { slot = 1, count = try100 },
                new { slot = 2, count = try50 },
                new { slot = 3, count = try20 },
                new { slot = 4, count = usd100 },
                new { slot = 5, notes = depositNotes },
            },
        }));

    private Task<HttpResponseMessage> ApproveAsync(Guid reconciliationId, string? user) =>
        _client.SendAsync(Request(HttpMethod.Post, $"/api/reconciliations/{reconciliationId}/approve", user, new { }));

    private static HttpRequestMessage Request(HttpMethod method, string url, string? user, object body)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(body, options: ApiFactory.Json),
        };
        if (user is not null)
        {
            request.Headers.Add("X-User", user);
        }

        return request;
    }
}
