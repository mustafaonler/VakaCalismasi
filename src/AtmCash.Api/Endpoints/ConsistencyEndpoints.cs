using AtmCash.Application.Consistency;

namespace AtmCash.Api.Endpoints;

internal static class ConsistencyEndpoints
{
    public static IEndpointRouteBuilder MapConsistencyEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api").WithTags("Consistency");

        group.MapGet("/consistency", (ConsistencyService service, CancellationToken ct) =>
                service.CheckAsync(null, ct))
            .WithSummary("Compare stored cassette balances with balances replayed from movements, for every ATM");

        group.MapGet("/atms/{atmId:guid}/consistency", (Guid atmId, ConsistencyService service, CancellationToken ct) =>
                service.CheckAsync(atmId, ct))
            .WithSummary("Compare stored cassette balances with balances replayed from movements, for one ATM");

        return routes;
    }
}
