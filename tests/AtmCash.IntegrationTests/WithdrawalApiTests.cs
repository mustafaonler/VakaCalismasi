using System.Net;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;
using AtmCash.Infrastructure.Persistence;
using AtmCash.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class WithdrawalApiTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Exact_quote_reserves_notes_and_confirm_dispenses_them()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 10), ("TRY 20", 10), ("USD 100", 10));

        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 170)).ReadAsync<QuoteDto>();

        Assert.Equal(QuoteStatus.Active, quote.Status);
        Assert.Equal(170, quote.Exact!.Amount);
        Assert.Equal(3, quote.Exact.NoteCount);
        Assert.Null(quote.Lower);
        Assert.Equal(3, (await _client.GetAtmAsync(atm.Id)).Cassettes.Sum(c => c.Reserved));

        var result = await (await _client.ConfirmAsync(quote.Id)).ReadAsync<WithdrawalResultDto>();

        Assert.Equal((QuoteOption.Exact, 170), (result.Option, result.Amount));
        var after = await _client.GetAtmAsync(atm.Id);
        Assert.Equal([9, 9, 9, 10], after.Cassettes.Take(4).Select(c => c.Count));
        Assert.All(after.Cassettes, c => Assert.Equal(0, c.Reserved));
    }

    [Fact]
    public async Task Repeated_confirm_with_same_key_returns_same_result_and_debits_once()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 10), ("TRY 20", 10), ("USD 100", 10));
        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 300)).ReadAsync<QuoteDto>();
        var key = Guid.NewGuid().ToString();

        var first = await _client.ConfirmAsync(quote.Id, key: key);
        var second = await _client.ConfirmAsync(quote.Id, key: key);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(7, (await _client.GetAtmAsync(atm.Id)).Cassettes.Single(c => c.Slot == 1).Count);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
        Assert.Equal(1, await db.Movements.CountAsync(m => m.CorrelationId == quote.Id && m.Type == MovementType.Withdrawal));
    }

    [Fact]
    public async Task Confirm_with_new_key_after_success_is_rejected()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 10));
        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 100)).ReadAsync<QuoteDto>();
        await (await _client.ConfirmAsync(quote.Id)).ReadAsync<WithdrawalResultDto>();

        await (await _client.ConfirmAsync(quote.Id)).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.QuoteAlreadyConfirmed);
    }

    [Fact]
    public async Task Idempotency_key_cannot_be_reused_for_another_quote()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 10));
        var first = await (await _client.QuoteAsync(atm.Id, "TRY", 100)).ReadAsync<QuoteDto>();
        var second = await (await _client.QuoteAsync(atm.Id, "TRY", 100)).ReadAsync<QuoteDto>();
        var key = Guid.NewGuid().ToString();
        await (await _client.ConfirmAsync(first.Id, key: key)).ReadAsync<WithdrawalResultDto>();

        await (await _client.ConfirmAsync(second.Id, key: key)).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.IdempotencyKeyReused);
    }

    [Fact]
    public async Task Confirm_requires_an_idempotency_key()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 10));
        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 100)).ReadAsync<QuoteDto>();

        var response = await _client.PostJsonAsync($"/api/withdrawal-quotes/{quote.Id}/confirm", new { });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.IdempotencyKeyRequired);
    }

    [Fact]
    public async Task Alternatives_are_offered_and_the_chosen_one_is_paid()
    {
        // Acceptance case 2 end to end: 50x1, 20x2, request 80.
        var atm = await _client.InServiceAtmAsync(("TRY 100", 0), ("TRY 50", 1), ("TRY 20", 2), ("USD 100", 10));

        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 80)).ReadAsync<QuoteDto>();

        Assert.Null(quote.Exact);
        Assert.Equal(70, quote.Lower!.Amount);
        Assert.Equal(90, quote.Upper!.Amount);
        await (await _client.ConfirmAsync(quote.Id)).AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.QuoteOptionRequired);

        var result = await (await _client.ConfirmAsync(quote.Id, "UPPER")).ReadAsync<WithdrawalResultDto>();

        Assert.Equal(90, result.Amount);
        Assert.All((await _client.GetAtmAsync(atm.Id)).Cassettes.Take(3), c => Assert.Equal(0, c.Count));
    }

    [Fact]
    public async Task Reserved_notes_are_not_offered_again_until_released()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 2), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 10));
        var first = await (await _client.QuoteAsync(atm.Id, "TRY", 200)).ReadAsync<QuoteDto>();

        await (await _client.QuoteAsync(atm.Id, "TRY", 100)).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.NoPayableAmount);

        var cancelled = await (await _client.PostJsonAsync($"/api/withdrawal-quotes/{first.Id}/cancel", new { })).ReadAsync<QuoteDto>();
        Assert.Equal(QuoteStatus.Cancelled, cancelled.Status);
        Assert.Equal(100, (await (await _client.QuoteAsync(atm.Id, "TRY", 100)).ReadAsync<QuoteDto>()).Exact!.Amount);
        await (await _client.ConfirmAsync(first.Id)).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.QuoteCancelled);
    }

    [Fact]
    public async Task Open_quotes_count_towards_the_daily_limit()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("USD 100", 20), ("USD 50", 10));
        var card = AtmScenario.NewCard();

        await (await _client.QuoteAsync(atm.Id, "USD", 1_000, card)).ReadAsync<QuoteDto>();

        await (await _client.QuoteAsync(atm.Id, "USD", 50, card)).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.DailyLimitExceeded);
        // Limits are per currency.
        await (await _client.QuoteAsync(atm.Id, "TRY", 100, card)).ReadAsync<QuoteDto>();
    }

    [Fact]
    public async Task Upper_alternative_above_the_remaining_limit_is_not_offered()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 500", 40), ("TRY 50", 1), ("TRY 20", 1), ("USD 100", 10));
        var card = AtmScenario.NewCard();
        var small = await (await _client.QuoteAsync(atm.Id, "TRY", 20, card)).ReadAsync<QuoteDto>();
        await (await _client.ConfirmAsync(small.Id)).ReadAsync<WithdrawalResultDto>();

        // Remaining limit is 19,980; the next payable amount above 19,960 is 20,000.
        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 19_960, card)).ReadAsync<QuoteDto>();

        Assert.Null(quote.Exact);
        Assert.Equal(19_550, quote.Lower!.Amount);
        Assert.Null(quote.Upper);
    }

    [Fact]
    public async Task Card_number_is_not_stored_in_plain_text()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 10));
        const string card = "4111111111111111";
        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 100, card)).ReadAsync<QuoteDto>();

        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AtmDbContext>().Quotes.SingleAsync(q => q.Id == quote.Id);

        Assert.DoesNotContain(card, stored.CardHash, StringComparison.Ordinal);
        Assert.Equal(64, stored.CardHash.Length);
    }

    [Fact]
    public async Task Expired_quote_cannot_be_confirmed_and_its_notes_are_released()
    {
        var clock = new TestClock();
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock))).CreateClient();
        var atm = await client.InServiceAtmAsync(("TRY 100", 5), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 10));
        var quote = await (await client.QuoteAsync(atm.Id, "TRY", 300)).ReadAsync<QuoteDto>();

        clock.Advance(TimeSpan.FromSeconds(61));

        await (await client.ConfirmAsync(quote.Id)).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.QuoteExpired);
        var after = await client.GetAtmAsync(atm.Id);
        Assert.Equal((5, 0), (after.Cassettes[0].Count, after.Cassettes[0].Reserved));
        Assert.Equal(QuoteStatus.Expired, (await (await client.GetAsync($"/api/withdrawal-quotes/{quote.Id}")).ReadAsync<QuoteDto>()).Status);
    }

    [Fact]
    public async Task Expiry_sweep_releases_reservations_of_overdue_quotes()
    {
        var clock = new TestClock();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var client = app.CreateClient();
        var atm = await client.InServiceAtmAsync(("TRY 100", 5), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 10));
        await (await client.QuoteAsync(atm.Id, "TRY", 500)).ReadAsync<QuoteDto>();

        clock.Advance(TimeSpan.FromSeconds(61));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<WithdrawalService>().ExpireOverdueQuotesAsync(default) >= 1);
        }

        Assert.Equal(0, (await client.GetAtmAsync(atm.Id)).Cassettes[0].Reserved);
    }

    [Fact]
    public async Task Entering_maintenance_cancels_open_quotes()
    {
        var atm = await _client.InServiceAtmAsync(("TRY 100", 5), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 10));
        var quote = await (await _client.QuoteAsync(atm.Id, "TRY", 200)).ReadAsync<QuoteDto>();

        var maintenance = await (await _client.PostJsonAsync($"/api/atms/{atm.Id}/status", new { status = "MAINTENANCE" }))
            .ReadAsync<Application.Atms.AtmDetailsDto>();

        Assert.Equal(0, maintenance.Cassettes[0].Reserved);
        Assert.Equal(QuoteStatus.Cancelled, (await (await _client.GetAsync($"/api/withdrawal-quotes/{quote.Id}")).ReadAsync<QuoteDto>()).Status);
        await (await _client.QuoteAsync(atm.Id, "TRY", 100)).AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
    }
}
