using AtmCash.Application.Abstractions;
using AtmCash.Infrastructure.Persistence;
using AtmCash.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString, string cardHashSecret)
    {
        if (string.IsNullOrWhiteSpace(cardHashSecret))
        {
            throw new ArgumentException("A card hash secret is required.", nameof(cardHashSecret));
        }

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICardHasher>(new HmacCardHasher(cardHashSecret));
        services.AddDbContext<AtmDbContext>(options => Configure(options, connectionString));
        services.AddScoped<IAtmDbContext>(sp => sp.GetRequiredService<AtmDbContext>());
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
}
