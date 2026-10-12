using System.Net;
using AtmCash.Application.Deposits;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Deposits;
using AtmCash.Infrastructure.Persistence;
using AtmCash.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class DepositApiTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Deposit_routes_notes_overflows_and_returns_what_does_not_fit()
    {
        // TRY 100 cassette has room for 5; deposit cassette has room for 3 after a first deposit of 97 EUR notes.
        var atm = await _client.InServiceAtmAsync(("TRY 100", 95), ("TRY 50", 10), ("TRY 20", 10), ("USD 100", 10));
        await (await DepositAsync(atm.Id, 0, ("EUR", 20, 97))).ReadAsync<DepositResultDto>();

        var result = await (await DepositAsync(atm.Id, 2, ("TRY", 100, 7), ("TRY", 50, 4), ("USD", 50, 3)))
            .ReadAsync<DepositResultDto>();

        Assert.Equal(
            [
                new DepositPlacement(1, CassetteKind.Recycling, Currency.TRY, 100, 5),
                new DepositPlacement(5, CassetteKind.Deposit, Currency.TRY, 100, 2),
                new DepositPlacement(2, CassetteKind.Recycling, Currency.TRY, 50, 4),
                new DepositPlacement(5, CassetteKind.Deposit, Currency.USD, 50, 1),
            ],
            result.Accepted);
        Assert.Equal(
            [
                new ReturnedNotes(ReturnReason.CassetteFull, Currency.USD, 50, 2),
                new ReturnedNotes(ReturnReason.Unrecognised, null, null, 2),
            ],
            result.Returned);
        Assert.Equal((12, 4), (result.AcceptedNoteCount, result.ReturnedNoteCount));
        Assert.Equal([new CurrencyAmountDto(Currency.TRY, 900), new CurrencyAmountDto(Currency.USD, 50)], result.AcceptedTotals);

        var after = await _client.GetAtmAsync(atm.Id);
        Assert.Equal([100, 14, 10, 10, 100], after.Cassettes.Select(c => c.Count));
        Assert.Equal(3, after.Cassettes[4].Notes.Count);
    }

    [Fact]
    public async Task Each_accepted_line_is_stored_as_a_deposit_movement_in_the_same_transaction()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 98), ("TRY 50", 10), ("TRY 20", 10), ("USD 100", 10));

        var result = await (await DepositAsync(atm.Id, 1, ("TRY", 100, 5), ("EUR", 50, 2))).ReadAsync<DepositResultDto>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
        var movements = await db.Movements.Where(m => m.CorrelationId == result.DepositId).ToListAsync();
        var atmEntity = await db.Atms.Include(a => a.Cassettes).SingleAsync(a => a.Id == atm.Id);
        var slotOf = atmEntity.Cassettes.ToDictionary(c => c.Id, c => c.Slot);

        Assert.All(movements, m => Assert.Equal(MovementType.Deposit, m.Type));
        Assert.Equal(
            [(1, Currency.TRY, 100, 2), (5, Currency.EUR, 50, 2), (5, Currency.TRY, 100, 3)],
            movements
                .Select(m => (slotOf[m.CassetteId], m.Currency, m.Denomination, m.Quantity))
                .OrderBy(m => m.Item1).ThenBy(m => m.Currency.ToString(), StringComparer.Ordinal));

        // Stored balances equal the sum of the cassette's movements.
        foreach (var cassette in atmEntity.Cassettes)
        {
            var fromLedger = await db.Movements
                .Where(m => m.CassetteId == cassette.Id && m.Type != MovementType.Reservation && m.Type != MovementType.ReservationRelease)
                .SumAsync(m => m.Quantity);
            Assert.Equal(cassette.Count, fromLedger);
        }
    }

    [Fact]
    public async Task Deposited_notes_in_a_recycling_cassette_can_be_withdrawn()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 0), ("TRY 50", 1), ("TRY 20", 0), ("USD 100", 1));
        await (await DepositAsync(atm.Id, 0, ("TRY", 100, 2))).ReadAsync<DepositResultDto>();

        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 200)).ReadAsync<QuoteDto>();

        Assert.Equal(200, quote.Exact!.Amount);
    }

    [Fact]
    public async Task Deposit_is_rejected_unless_the_atm_is_in_service()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 1), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0));
        await _client.PostJsonAsync($"/api/atms/{atm.Id}/status", new { status = "MAINTENANCE" });

        var response = await DepositAsync(atm.Id, 0, ("TRY", 100, 1));

        await response.AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
    }

    [Fact]
    public async Task Invalid_deposits_are_rejected_without_changing_the_atm()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 1), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0));

        await (await DepositAsync(atm.Id, 0, ("TRY", 200, 1))).AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidDenomination);
        await (await DepositAsync(atm.Id, 0)).AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.EmptyDeposit);
        await (await DepositAsync(atm.Id, -1, ("TRY", 100, 1))).AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidQuantity);
        await (await DepositAsync(Guid.NewGuid(), 0, ("TRY", 100, 1))).AssertProblemAsync(HttpStatusCode.NotFound, ErrorCodes.AtmNotFound);

        Assert.Equal(1, (await _client.GetAtmAsync(atm.Id)).Cassettes.Sum(c => c.Count));
    }

    [Fact]
    public async Task Parallel_deposits_never_overfill_the_deposit_cassette()
    {
        // No EUR recycling cassette: every EUR note goes to the deposit cassette (capacity 100).
        var atm = await _client.InServiceAtmAsync(("TRY 100", 1), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0));

        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await (await DepositAsync(atm.Id, 0, ("EUR", 20, 15))).ReadAsync<DepositResultDto>();
            }))
            .ToList();
        start.Set();
        var results = await Task.WhenAll(requests);

        Assert.Equal(100, results.Sum(r => r.AcceptedNoteCount));
        Assert.Equal(50, results.Sum(r => r.ReturnedNoteCount));
        Assert.Equal(100, (await _client.GetAtmAsync(atm.Id)).Cassettes[4].Count);
    }

    private Task<HttpResponseMessage> DepositAsync(Guid atmId, int unrecognised, params (string Currency, int Denomination, int Count)[] notes) =>
        _client.PostJsonAsync($"/api/atms/{atmId}/deposits", new
        {
            notes = notes.Select(n => new { currency = n.Currency, denomination = n.Denomination, count = n.Count }),
            unrecognisedCount = unrecognised,
        });
}
