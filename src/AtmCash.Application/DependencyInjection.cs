using AtmCash.Application.Atms;
using AtmCash.Application.Withdrawals;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AtmService>();
        services.AddScoped<WithdrawalService>();
        return services;
    }
}
