using AtmCash.Application.Atms;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AtmCash.Api.Endpoints;

public sealed record CreateAtmRequest(string Code);

public sealed record CassetteSpecRequest(Currency Currency, int Denomination);

/// <param name="Cassettes">The four recycling cassettes, in slot order 1-4. Slot 5 is always the deposit cassette.</param>
public sealed record ConfigureCassettesRequest(IReadOnlyList<CassetteSpecRequest>? Cassettes);

public sealed record CassetteLoadRequest(int Slot, int Quantity);

public sealed record LoadCashRequest(IReadOnlyList<CassetteLoadRequest>? Loads);

public sealed record UnloadCashRequest(IReadOnlyList<int>? Slots);

public sealed record ChangeStatusRequest(AtmStatus Status);

internal static class AtmEndpoints
{
    public static IEndpointRouteBuilder MapAtmEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/atms").WithTags("ATMs");

        group.MapPost("/", async Task<Created<AtmDetailsDto>> (CreateAtmRequest request, AtmService service, CancellationToken ct) =>
            {
                var atm = await service.CreateAsync(request.Code, ct);
                return TypedResults.Created($"/api/atms/{atm.Id}", atm);
            })
            .WithSummary("Create an ATM (starts UNCONFIGURED)");

        group.MapGet("/", (AtmService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("List ATMs");

        group.MapGet("/{atmId:guid}", (Guid atmId, AtmService service, CancellationToken ct) => service.GetAsync(atmId, ct))
            .WithSummary("Notes per cassette and denomination, and the balance per currency");

        group.MapPut("/{atmId:guid}/cassettes", (Guid atmId, ConfigureCassettesRequest request, AtmService service, CancellationToken ct) =>
                service.ConfigureCassettesAsync(
                    atmId,
                    (request.Cassettes ?? []).Select(c => new RecyclingCassetteSpec(c.Currency, c.Denomination)).ToList(),
                    ct))
            .WithSummary("Configure the four recycling cassettes");

        group.MapPost("/{atmId:guid}/cash-loads", (Guid atmId, LoadCashRequest request, AtmService service, CancellationToken ct) =>
                service.LoadCashAsync(
                    atmId,
                    (request.Loads ?? []).Select(l => new CassetteLoad(l.Slot, l.Quantity)).ToList(),
                    ct))
            .WithSummary("Record cash loaded by hand into recycling cassettes");

        group.MapPost("/{atmId:guid}/cash-unloads", (Guid atmId, UnloadCashRequest request, AtmService service, CancellationToken ct) =>
                service.UnloadAsync(atmId, request.Slots ?? [], ct))
            .WithSummary("Empty the given cassettes (e.g. collect the deposit cassette)");

        group.MapPost("/{atmId:guid}/status", (Guid atmId, ChangeStatusRequest request, AtmService service, CancellationToken ct) =>
                service.ChangeStatusAsync(atmId, request.Status, ct))
            .WithSummary("Move the ATM to IN_SERVICE or MAINTENANCE");

        return routes;
    }
}
