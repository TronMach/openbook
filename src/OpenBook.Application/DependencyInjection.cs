using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;

namespace OpenBook.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenBookApplication(this IServiceCollection services)
    {
        services.AddScoped<PostingEngine>();
        services.AddScoped<TrialBalanceQuery>();
        services.AddScoped<AccountService>();
        services.AddScoped<CompanySetupService>();
        services.AddScoped<AuthService>();
        return services;
    }
}
