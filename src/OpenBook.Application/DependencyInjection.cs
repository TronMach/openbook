using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Attachments;
using OpenBook.Application.Auditing;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Search;
using OpenBook.Application.Sales;

namespace OpenBook.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenBookApplication(this IServiceCollection services)
    {
        services.AddScoped<ClosedPeriodOverride>();
        services.AddScoped<PostingEngine>();
        services.AddScoped<TrialBalanceQuery>();
        services.AddScoped<CloseBooksService>();
        services.AddScoped<RecurringEntryService>();
        services.AddScoped<OpeningBalanceService>();
        services.AddScoped<AuditLogQuery>();
        services.AddScoped<ReportLedger>();
        services.AddScoped<FinancialReports>();
        services.AddScoped<SearchService>();
        services.AddScoped<AccountService>();
        services.AddScoped<ChartImportService>();
        services.AddScoped<ChartTemplates>();
        services.AddScoped<BankingService>();
        services.AddScoped<BankImportService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<BankFeedService>();
        services.AddScoped<BankRuleService>();
        services.AddScoped<CheckService>();
        services.AddScoped<SalesAccounts>();
        services.AddScoped<SalesLineBuilder>();
        services.AddScoped<EstimateService>();
        services.AddScoped<CreditService>();
        services.AddScoped<DepositService>();
        services.AddScoped<RecurringInvoiceService>();
        services.AddScoped<ReceivablesService>();
        services.AddScoped<FormService>();
        services.AddScoped<EmailService>();
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
        services.AddScoped<VendorCreditService>();
        services.AddScoped<RecurringBillService>();
        services.AddScoped<PayablesService>();
        services.AddScoped<AttachmentService>();
        services.AddScoped<VendorImportService>();
        services.AddScoped<CompanySetupService>();
        services.AddScoped<CompanySettingsService>();
        services.AddScoped<AuthService>();
        return services;
    }
}
