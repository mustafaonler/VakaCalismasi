using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Withdrawals;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Endpoints;

public sealed record QuoteRequest(string CardNumber, Currency Currency, int Amount);

/// <param name="Option">EXACT, LOWER or UPPER. May be omitted when the quote has an exact match.</param>
public sealed record ConfirmQuoteRequest(QuoteOption? Option);

internal static class WithdrawalEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapWithdrawalEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(
                "/api/atms/{atmId:guid}/withdrawal-quotes",
                async Task<Created<QuoteDto>> (Guid atmId, QuoteRequest request, WithdrawalService service, CancellationToken ct) =>
                {
                    var quote = await service.RequestQuoteAsync(atmId, request.CardNumber, request.Currency, request.Amount, ct);
                    return TypedResults.Created($"/api/withdrawal-quotes/{quote.Id}", quote);
                })
            .WithTags("Withdrawals")
            .WithSummary("Quote a withdrawal: exact breakdown or lower/upper alternatives, reserved for 60 seconds");

        var quotes = routes.MapGroup("/api/withdrawal-quotes").WithTags("Withdrawals");

        quotes.MapGet("/{quoteId:guid}", (Guid quoteId, WithdrawalService service, CancellationToken ct) =>
                service.GetQuoteAsync(quoteId, ct))
            .WithSummary("Get a quote");

        quotes.MapPost(
                "/{quoteId:guid}/confirm",
                (Guid quoteId,
                    [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
                    ConfirmQuoteRequest? request,
                    WithdrawalService service,
                    CancellationToken ct) =>
                    service.ConfirmAsync(quoteId, request?.Option, idempotencyKey, ct))
            .WithSummary("Confirm a quote and dispense the cash (requires an Idempotency-Key header)");

        quotes.MapPost("/{quoteId:guid}/cancel", (Guid quoteId, WithdrawalService service, CancellationToken ct) =>
                service.CancelAsync(quoteId, ct))
            .WithSummary("Cancel a quote and release its reservation");

        return routes;
    }
}
