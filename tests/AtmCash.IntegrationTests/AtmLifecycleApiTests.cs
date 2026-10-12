using System.Net;
using AtmCash.Application.Atms;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Infrastructure.Persistence;
using AtmCash.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public sealed class AtmLifecycleApiTests(ApiFactory factory)
{
    private static readonly object StandardCassettes = new
    {
        cassettes = new object[]
        {
            new { currency = "TRY", denomination = 100 },
            new { currency = "TRY", denomination = 50 },
            new { currency = "TRY", denomination = 20 },
            new { currency = "USD", denomination = 100 },
        },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_check_reports_healthy_database()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Atm_goes_from_creation_to_service_and_maintenance()
    {
        var atm = await CreateAtmAsync();
        Assert.Equal(AtmStatus.Unconfigured, atm.Status);

        atm = await (await _client.PutJsonAsync($"/api/atms/{atm.Id}/cassettes", StandardCassettes)).ReadAsync<AtmDetailsDto>();
        Assert.Equal(AtmStatus.Configured, atm.Status);
        Assert.Equal(5, atm.Cassettes.Count);

        atm = await (await _client.PostJsonAsync($"/api/atms/{atm.Id}/cash-loads", new
        {
            loads = new[] { new { slot = 1, quantity = 40 }, new { slot = 3, quantity = 25 }, new { slot = 4, quantity = 10 } },
        })).ReadAsync<AtmDetailsDto>();
        Assert.Equal(AtmStatus.Loaded, atm.Status);

        atm = await (await _client.PostJsonAsync($"/api/atms/{atm.Id}/status", new { status = "IN_SERVICE" })).ReadAsync<AtmDetailsDto>();
        Assert.Equal(AtmStatus.InService, atm.Status);

        atm = await (await _client.PostJsonAsync($"/api/atms/{atm.Id}/status", new { status = "MAINTENANCE" })).ReadAsync<AtmDetailsDto>();
        Assert.Equal(AtmStatus.Maintenance, atm.Status);

        var details = await (await _client.GetAsync($"/api/atms/{atm.Id}")).ReadAsync<AtmDetailsDto>();
        var tryBalance = Assert.Single(details.Balances, b => b.Currency == Currency.TRY);
        Assert.Equal(4_500, tryBalance.Total);
        Assert.Equal(65, tryBalance.NoteCount);
        Assert.Equal(1_000, Assert.Single(details.Balances, b => b.Currency == Currency.USD).Total);
        Assert.Equal(40, details.Cassettes.Single(c => c.Slot == 1).Count);
    }

    [Fact]
    public async Task Every_load_is_persisted_as_a_movement()
    {
        var atm = await ConfiguredAtmAsync();
        await (await _client.PostJsonAsync($"/api/atms/{atm.Id}/cash-loads", new
        {
            loads = new[] { new { slot = 2, quantity = 30 } },
        })).ReadAsync<AtmDetailsDto>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();
        var movement = await db.Movements.SingleAsync(m => m.AtmId == atm.Id);

        Assert.Equal((MovementType.Load, Currency.TRY, 50, 30), (movement.Type, movement.Currency, movement.Denomination, movement.Quantity));
        Assert.NotEqual(default, movement.CreatedAt);
    }

    [Fact]
    public async Task Movement_ledger_is_append_only_in_the_database()
    {
        var atm = await ConfiguredAtmAsync();
        await (await _client.PostJsonAsync($"/api/atms/{atm.Id}/cash-loads", new
        {
            loads = new[] { new { slot = 1, quantity = 5 } },
        })).ReadAsync<AtmDetailsDto>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtmDbContext>();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE cassette_movements SET quantity = 999 WHERE atm_id = {atm.Id}"));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlAsync($"DELETE FROM cassette_movements WHERE atm_id = {atm.Id}"));
    }

    [Fact]
    public async Task Invalid_transition_is_rejected_with_problem_details()
    {
        var atm = await ConfiguredAtmAsync();

        var response = await _client.PostJsonAsync($"/api/atms/{atm.Id}/status", new { status = "IN_SERVICE" });

        await response.AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public async Task Operation_in_wrong_state_is_rejected()
    {
        var atm = await CreateAtmAsync();

        var response = await _client.PostJsonAsync($"/api/atms/{atm.Id}/cash-loads", new
        {
            loads = new[] { new { slot = 1, quantity = 5 } },
        });

        await response.AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
    }

    [Fact]
    public async Task Invalid_configuration_is_rejected()
    {
        var atm = await CreateAtmAsync();

        var response = await _client.PutJsonAsync($"/api/atms/{atm.Id}/cassettes", new
        {
            cassettes = new[]
            {
                new { currency = "TRY", denomination = 100 },
                new { currency = "TRY", denomination = 50 },
                new { currency = "TRY", denomination = 20 },
                new { currency = "TRY", denomination = 500 },
            },
        });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ForeignCassetteCount);
    }

    [Fact]
    public async Task Unknown_atm_returns_not_found()
    {
        var response = await _client.GetAsync($"/api/atms/{Guid.NewGuid()}");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, ErrorCodes.AtmNotFound);
    }

    [Fact]
    public async Task Duplicate_code_is_rejected()
    {
        var atm = await CreateAtmAsync();

        var response = await _client.PostJsonAsync("/api/atms", new { code = atm.Code });

        await response.AssertProblemAsync(HttpStatusCode.Conflict, ErrorCodes.AtmCodeTaken);
    }

    [Fact]
    public async Task Malformed_body_is_reported_as_problem_details()
    {
        var atm = await CreateAtmAsync();

        var response = await _client.PostJsonAsync($"/api/atms/{atm.Id}/status", new { status = "FLYING" });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "INVALID_REQUEST");
    }

    private async Task<AtmDetailsDto> CreateAtmAsync() =>
        await (await _client.PostJsonAsync("/api/atms", new { code = $"ATM-{Guid.NewGuid():N}"[..16] })).ReadAsync<AtmDetailsDto>();

    private async Task<AtmDetailsDto> ConfiguredAtmAsync()
    {
        var atm = await CreateAtmAsync();
        return await (await _client.PutJsonAsync($"/api/atms/{atm.Id}/cassettes", StandardCassettes)).ReadAsync<AtmDetailsDto>();
    }
}
