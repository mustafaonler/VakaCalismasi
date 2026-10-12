using AtmCash.Application.Atms;

namespace AtmCash.IntegrationTests.Infrastructure;

internal static class AtmScenario
{
    /// <summary>
    /// Creates an ATM through the API, configures the four recycling cassettes as given
    /// (e.g. "TRY 100"), loads the given counts and puts it in service.
    /// </summary>
    public static async Task<AtmDetailsDto> InServiceAtmAsync(this HttpClient client, params (string Cassette, int Count)[] cassettes)
    {
        var atm = await (await client.PostJsonAsync("/api/atms", new { code = $"ATM-{Guid.NewGuid():N}"[..16] }))
            .ReadAsync<AtmDetailsDto>();

        await (await client.PutJsonAsync($"/api/atms/{atm.Id}/cassettes", new
        {
            cassettes = cassettes.Select(c =>
            {
                var parts = c.Cassette.Split(' ');
                return new { currency = parts[0], denomination = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) };
            }),
        })).ReadAsync<AtmDetailsDto>();

        await (await client.PostJsonAsync($"/api/atms/{atm.Id}/cash-loads", new
        {
            loads = cassettes
                .Select((c, i) => new { slot = i + 1, quantity = c.Count })
                .Where(l => l.quantity > 0),
        })).ReadAsync<AtmDetailsDto>();

        return await (await client.PostJsonAsync($"/api/atms/{atm.Id}/status", new { status = "IN_SERVICE" }))
            .ReadAsync<AtmDetailsDto>();
    }

    public static Task<AtmDetailsDto> GetAtmAsync(this HttpClient client, Guid atmId) =>
        client.GetAsync($"/api/atms/{atmId}").ContinueWith(t => t.Result.ReadAsync<AtmDetailsDto>()).Unwrap();

    public static Task<HttpResponseMessage> QuoteAsync(this HttpClient client, Guid atmId, string currency, int amount, string? card = null) =>
        client.PostJsonAsync($"/api/atms/{atmId}/withdrawal-quotes", new { cardNumber = card ?? NewCard(), currency, amount });

    public static Task<HttpResponseMessage> ConfirmAsync(this HttpClient client, Guid quoteId, string? option = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/withdrawal-quotes/{quoteId}/confirm")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { option }, options: ApiFactory.Json),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    /// <summary>A random 16-digit card number, so tests do not share daily limits.</summary>
    public static string NewCard() => string.Concat(Enumerable.Range(0, 16).Select(_ => Random.Shared.Next(10)));
}
