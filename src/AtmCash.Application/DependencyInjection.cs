using AtmCash.Application.Atms;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AtmService>();
        return services;
    }
}
