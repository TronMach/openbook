using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;

namespace OpenBook.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenBookApplication(this IServiceCollection services, LedgerOptions options)
    {
        services.AddSingleton(options);
        services.AddScoped<PostingEngine>();
        services.AddScoped<TrialBalanceQuery>();
        services.AddScoped<AccountService>();
        return services;
    }
}
