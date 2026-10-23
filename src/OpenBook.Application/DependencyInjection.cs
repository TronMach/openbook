using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;

namespace OpenBook.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenBookApplication(this IServiceCollection services)
    {
        services.AddScoped<PostingEngine>();
        services.AddScoped<TrialBalanceQuery>();
        services.AddScoped<AccountService>();
        services.AddScoped<ChartImportService>();
        services.AddScoped<BankingService>();
        services.AddScoped<BankImportService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<SalesAccounts>();
        services.AddScoped<CustomerService>();
        services.AddScoped<ItemService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<SalesReceiptService>();
        services.AddScoped<CustomerImportService>();
        services.AddScoped<ItemImportService>();
        services.AddScoped<PurchaseAccounts>();
        services.AddScoped<VendorService>();
        services.AddScoped<BillService>();
        services.AddScoped<BillPaymentService>();
        services.AddScoped<VendorImportService>();
        services.AddScoped<CompanySetupService>();
        services.AddScoped<CompanySettingsService>();
        services.AddScoped<AuthService>();
        return services;
    }
}
