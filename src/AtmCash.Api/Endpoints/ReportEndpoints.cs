using AtmCash.Application.Reports;

namespace AtmCash.Api.Endpoints;

internal static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/reports/operations", (Guid? atmId, ReportService service, CancellationToken ct) =>
                service.GetOperationsReportAsync(atmId, ct))
            .WithTags("Reports")
            .WithSummary("Note, reserved and available counts per ATM and cassette, totals per currency and overall in TRY")
            .WithDescription("Optional ?atmId= limits the report to one ATM.");

        return routes;
    }
}
