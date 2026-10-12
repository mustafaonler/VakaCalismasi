using AtmCash.Application.Deposits;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Deposits;

namespace AtmCash.Api.Endpoints;

public sealed record DepositNoteRequest(Currency Currency, int Denomination, int Count);

/// <param name="Notes">Recognised notes per currency and denomination.</param>
/// <param name="UnrecognisedCount">Notes the machine could not recognise; they are returned to the customer.</param>
public sealed record DepositRequest(IReadOnlyList<DepositNoteRequest>? Notes, int UnrecognisedCount);

internal static class DepositEndpoints
{
    public static IEndpointRouteBuilder MapDepositEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(
                "/api/atms/{atmId:guid}/deposits",
                (Guid atmId, DepositRequest request, DepositService service, CancellationToken ct) =>
                    service.DepositAsync(
                        atmId,
                        (request.Notes ?? []).Select(n => new DepositNote(n.Currency, n.Denomination, n.Count)).ToList(),
                        request.UnrecognisedCount,
                        ct))
            .WithTags("Deposits")
            .WithSummary("Deposit notes: shows the cassette each note went to and the notes returned to the customer");

        return routes;
    }
}
