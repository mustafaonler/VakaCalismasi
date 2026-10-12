using System.Net;
using AtmCash.Domain.Atms;
using AtmCash.Infrastructure.Persistence;
using AtmCash.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.IntegrationTests;

/// <summary>B6: parallel quote requests must never reserve the same physical note twice.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ConcurrencyTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Parallel_quotes_on_one_atm_never_reserve_the_same_note_twice()
    {
        // 10 notes of TRY 100 and 30 customers asking for TRY 100 at the same moment.
        var atm = await _client.InServiceAtmAsync(("TRY 100", 10), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0));

        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 30)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await _client.QuoteAsync(atm.Id, "TRY", 100);
            }))
            .ToList();
        start.Set();
        var responses = await Task.WhenAll(requests);

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var cassette = (await _client.GetAtmAsync(atm.Id)).Cassettes.Single(c => c.Slot == 1);
        Assert.Equal((10, 10, 0), (cassette.Count, cassette.Reserved, cassette.Available));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
        Assert.Equal(10, await db.Movements
            .Where(m => m.AtmId == atm.Id && m.Type == MovementType.Reservation)
            .SumAsync(m => m.Quantity));
    }

    [Fact]
    public async Task Parallel_quotes_for_one_card_cannot_exceed_its_daily_limit()
    {
        // Two ATMs, one card, TRY 20,000 limit: 30 parallel requests of TRY 1,000 across both ATMs.
        var atms = new[]
        {
            await _client.InServiceAtmAsync(("TRY 500", 100), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0)),
            await _client.InServiceAtmAsync(("TRY 500", 100), ("TRY 50", 0), ("TRY 20", 0), ("USD 100", 0)),
        };
        var card = AtmScenario.NewCard();

        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 30)
            .Select(i => Task.Run(async () =>
            {
                start.Wait();
                return await _client.QuoteAsync(atms[i % 2].Id, "TRY", 1_000, card);
            }))
            .ToList();
        start.Set();
        var responses = await Task.WhenAll(requests);

        Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }
}
