using AtmCash.Application.Atms;
using AtmCash.Application.Consistency;
using AtmCash.Application.Deposits;
using AtmCash.Application.Reconciliations;
using AtmCash.Application.Reports;
using AtmCash.Application.Withdrawals;
using Microsoft.Extensions.DependencyInjection;

namespace AtmCash.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AtmService>();
        services.AddScoped<WithdrawalService>();
        services.AddScoped<DepositService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<ConsistencyService>();
        services.AddScoped<ReportService>();
        return services;
    }
}
