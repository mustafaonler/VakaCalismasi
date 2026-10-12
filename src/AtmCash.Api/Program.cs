using System.Text.Json;
using System.Text.Json.Serialization;
using AtmCash.Api.Background;
using AtmCash.Api.Endpoints;
using AtmCash.Api.Errors;
using AtmCash.Application;
using AtmCash.Infrastructure;
using AtmCash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

builder.Services
    .AddApplication()
    .AddInfrastructure(
        connectionString,
        builder.Configuration["Security:CardHashSecret"]
            ?? throw new InvalidOperationException("Security:CardHashSecret is not configured."));
builder.Services.AddHostedService<QuoteExpiryService>();

// Enums travel as the spec spells them: IN_SERVICE, RESERVATION_RELEASE, TRY ...
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));

// Malformed bodies throw so that they are reported through the same ProblemDetails model.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AtmDbContext>("database");

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AtmDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ATM Cash Management API"));
app.MapHealthChecks("/health");

app.MapAtmEndpoints();
app.MapWithdrawalEndpoints();
app.MapDepositEndpoints();
app.MapReconciliationEndpoints();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
