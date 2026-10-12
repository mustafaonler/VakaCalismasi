using AtmCash.Application.Reconciliations;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Reconciliations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Endpoints;

public sealed record CountedNoteRequest(Currency Currency, int Denomination, int Count);

/// <param name="Slot">Cassette slot 1-5.</param>
/// <param name="Count">Counted notes of a recycling cassette (slots 1-4).</param>
/// <param name="Notes">Counted notes of the deposit cassette (slot 5); an empty list means empty.</param>
public sealed record CassetteCountRequest(int Slot, int? Count, IReadOnlyList<CountedNoteRequest>? Notes);

public sealed record ReconcileRequest(IReadOnlyList<CassetteCountRequest>? Cassettes);

internal static class ReconciliationEndpoints
{
    public const string UserHeader = "X-User";

    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(
                "/api/atms/{atmId:guid}/reconciliations",
                async Task<Created<ReconciliationDto>> (
                    Guid atmId,
                    [FromHeader(Name = UserHeader)] string? user,
                    ReconcileRequest request,
                    ReconciliationService service,
                    CancellationToken ct) =>
                {
                    var counts = (request.Cassettes ?? [])
                        .Select(c => new CassetteCount(
                            c.Slot,
                            c.Count,
                            c.Notes?.Select(n => new CountedNote(n.Currency, n.Denomination, n.Count)).ToList()))
                        .ToList();
                    var reconciliation = await service.ReconcileAsync(atmId, user, counts, ct);
                    return TypedResults.Created($"/api/reconciliations/{reconciliation.Id}", reconciliation);
                })
            .WithTags("Reconciliation")
            .WithSummary("Record a manual count (MAINTENANCE only); differences over TRY 1,000 need a second user's approval");

        var reconciliations = routes.MapGroup("/api/reconciliations").WithTags("Reconciliation");

        reconciliations.MapGet("/{reconciliationId:guid}", (Guid reconciliationId, ReconciliationService service, CancellationToken ct) =>
                service.GetAsync(reconciliationId, ct))
            .WithSummary("Get a reconciliation");

        reconciliations.MapPost(
                "/{reconciliationId:guid}/approve",
                (Guid reconciliationId, [FromHeader(Name = UserHeader)] string? user, ReconciliationService service, CancellationToken ct) =>
                    service.ApproveAsync(reconciliationId, user, ct))
            .WithSummary("Approve a reconciliation; X-User must differ from the user who performed it");

        routes.MapGet("/api/exchange-rates", (ReconciliationService service, CancellationToken ct) =>
                service.ListExchangeRatesAsync(ct))
            .WithTags("Reconciliation")
            .WithSummary("Exchange rates used to value differences in TRY");

        return routes;
    }
}
