using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Assets;
using OpenBook.Application.Banking;
using OpenBook.Application.Channels;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Settlements;
using OpenBook.Application.Companies;
using OpenBook.Application.CustomFields;
using OpenBook.Application.Documents;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Pricing;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Application.Segments;
using OpenBook.Application.Webhooks;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Companies;
using OpenBook.Domain.CustomFields;
using OpenBook.Domain.Documents;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;
using OpenBook.Domain.Segments;

namespace OpenBook.Integration.Tests.Browser;

/// <summary>
/// One record of each kind the demo generator doesn't make, entered through the services on top of its year of history,
/// so the accessibility crawl finds a page of every kind (<see cref="DemoBrowserApp"/>). Dated a few weeks back.
/// </summary>
internal static class EveryRecordKind
{
    /// <returns>
    /// Pages no link on another page leads to: for someone not signed in, the invitation and the customer portal (only
    /// their tokens reach them); signed in, an expense's edit page and a donor's statement email.
    /// </returns>
    public static async Task<(IReadOnlyList<string> SignedOut, IReadOnlyList<string> SignedIn)> AddAsync(IServiceProvider services)
    {
        async Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull
        {
            await using var scope = services.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<TService>());
        }

        async Task Do<TService>(Func<TService, Task> work) where TService : notnull
        {
            await using var scope = services.CreateAsyncScope();
            await work(scope.ServiceProvider.GetRequiredService<TService>());
        }

        var today = CompanyTime.Today;
        var d = today.AddDays(-30);
        var chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        Guid Account(string name) => chart.FindByFullName(name)?.Id ?? throw new InvalidOperationException($"No account '{name}'.");
        var checking = Account("Checking");
        var card = Account("Business Credit Card");
        var supplies = Account("Office Supplies");
        var income = Account("Service Revenue");
        Task<Account> NewAccount(string name, AccountType type, bool restricted = false) =>
            In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand(name, type, DonorRestricted: restricted)));
        var savings = await NewAccount("Reserve account", AccountType.Bank);
        var equipment = await NewAccount("Shop fittings", AccountType.FixedAsset);
        var loanAccount = await NewAccount("Van loan account", AccountType.LongTermLiability);

        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Every Kind Ltd", Email: "ap@everykind.test")));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Kind Supplies")));
        var service = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Advice", ItemType.Service, income, 100m)));
        var part = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Bracket", ItemType.Inventory, income, 20m, PurchaseCost: 5m)));
        var kit = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Bracket kit", ItemType.Assembly, income, 90m)));
        await Do<ItemService>(s => s.SetComponentsAsync(kit.Id, [(part.Id, 4m)]));
        SalesLineDraft Line() => new(service.Id, null, 1m, 100m);

        // Sales.
        await Do<EstimateService>(s => s.CreateAsync(new SaveEstimateCommand(customer.Id, d, [Line()])));
        await Do<SalesOrderService>(s => s.CreateAsync(new SaveSalesOrderCommand(customer.Id, d, [Line()])));
        await Do<SalesReceiptService>(s => s.CreateAsync(new CreateSalesReceiptCommand(customer.Id, d, checking, [Line()])));
        await Do<RecurringInvoiceService>(s => s.CreateAsync(new RecurringInvoiceDetails("Monthly advice", customer.Id, [Line()], RecurrenceFrequency.Monthly, today.AddDays(10))));
        var credit = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(customer.Id, d, [Line()])));
        await Do<CreditService>(s => s.RefundAsync(new RefundCreditCommand(CreditKind.CreditMemo, credit.Id, d.AddDays(1), checking, 40m)));
        await Do<RetainerService>(s => s.ReceiveAsync(new ReceiveRetainerCommand(customer.Id, d, checking, 500m)));
        var unpaid = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, d.AddDays(-60), [Line()])));
        await Do<WriteOffService>(s => s.WriteOffAsync(new WriteOffCommand(d, [new WriteOffLine(unpaid.Id)])));
        var portal = await In<CustomerPortalService, (PortalAccess Access, string Token)>(s => s.CreateAsync(customer.Id));

        // Purchases.
        await Do<PurchaseOrderService>(s => s.CreateAsync(new SavePurchaseOrderCommand(vendor.Id, d, [new PurchaseOrderLineDraft(null, supplies, "Paper", 1m, 30m)])));
        await Do<RecurringBillService>(s => s.CreateAsync(new RecurringBillDetails("Office rent", vendor.Id, [new BillLineDraft(supplies, 1500m)], RecurrenceFrequency.Monthly, today.AddDays(10))));
        var vendorCredit = await In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(vendor.Id, d, [new BillLineDraft(supplies, 75m)])));
        await Do<VendorCreditService>(s => s.RecordRefundAsync(new RecordVendorRefundCommand(vendorCredit.Id, d.AddDays(1), checking, 25m)));
        await Do<VendorDepositService>(s => s.CreateAsync(new CreateVendorDepositCommand(vendor.Id, d, 200m, checking)));
        await Do<ReceiptService>(s => s.UploadAsync("receipt.txt", new MemoryStream(Encoding.UTF8.GetBytes($"Kind Supplies\n{d:yyyy-MM-dd}\nTotal 12.50"))));
        await Do<DocumentCaptureService>(s => s.UploadAsync(DocumentKind.PackingSlip, "slip.txt", new MemoryStream(Encoding.UTF8.GetBytes("Packing slip\nBracket 4")), useClaude: false));

        // Inventory: stock in, a build, a landed cost, a transfer.
        var stockBill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, d.AddDays(-10), [new BillLineDraft(Guid.Empty, 200m, null, part.Id, 40m)])));
        await Do<InventoryAdjustmentService>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(d.AddDays(-9), [new InventoryAdjustmentLineDraft(part.Id, 2m, 5m)])));
        await Do<AssemblyBuildService>(s => s.CreateAsync(new BuildAssemblyCommand(kit.Id, 1m, d)));
        await Do<LandedCostService>(s => s.CreateAsync(new CreateLandedCostCommand(d, 20m, [stockBill.Id])));
        var east = await In<WarehouseService, Warehouse>(s => s.CreateAsync("East"));
        await Do<StockTransferService>(s => s.CreateAsync(new CreateStockTransferCommand(d.AddDays(1), [new StockTransferLineDraft(part.Id, 4m, StockPlace.Default, new StockPlace(east.Id, null))])));

        // Banking.
        await Do<BankingService>(s => s.TransferAsync(new TransferCommand(checking, savings.Id, d, 100m)));
        var expense = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, checking, d, [new BankSplit(supplies, 18.40m)], VendorId: vendor.Id)));
        await Do<RecurringExpenseService>(s => s.CreateAsync(new RecurringExpenseDetails("Design software", card, [new BankSplit(supplies, 29.99m, "Monthly plan")], RecurrenceFrequency.Monthly, today.AddDays(10))));
        await Do<BankRuleService>(s => s.CreateAsync(new BankRuleDetails("Paper", "paper co", supplies, RuleDirection.MoneyOut)));
        var reconciliation = await In<ReconciliationService, Reconciliation>(s => s.StartAsync(savings.Id, today, 0m));
        await Do<ReconciliationService>(s => s.CompleteAsync(reconciliation.Id));
        await Do<PettyCashService>(s => s.CreateAsync(new CreatePettyCashFundCommand(null, "Front desk box", 200m, null, FundFromAccountId: checking)));
        await using (var scope = services.CreateAsyncScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<OpenBook.Application.Forms.ISecretProtector>();
            var store = scope.ServiceProvider.GetRequiredService<IBankingStore>();
            store.Add(BankConnection.Create(BankConnectionProvider.SimpleFin, "Local bank", protector.Protect("https://bridge.test/token"), DateTimeOffset.UtcNow));
            await store.SaveChangesAsync(default);
        }

        // Journal, assets and loans.
        await Do<RecurringEntryService>(s => s.CreateAsync(new RecurringEntryDetails("Office rent accrual",
            [JournalLineDraft.Dr(supplies, 1200m), JournalLineDraft.Cr(checking, 1200m)], RecurrenceFrequency.Monthly, today.AddDays(10))));
        await Do<FixedAssetService>(s => s.CreateAsync(new FixedAssetDetails("Delivery van", equipment.Id, Guid.Empty, Guid.Empty, today.AddMonths(-6), 3600m, DepreciationMethod.StraightLine, 36)));
        await Do<DepreciationService>(s => s.RunAsync(today.AddDays(-1)));
        await Do<LoanService>(s => s.CreateAsync(new CreateLoanCommand(new LoanDetails("Van loan", loanAccount.Id, Guid.Empty, 10_000m, 6m, 12, today.AddDays(10)))));

        // Sales tax.
        var agency = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("State Revenue")));
        var rate = await In<SalesTaxService, TaxRate>(s => s.CreateRateAsync(new TaxRateDetails("State tax", agency.Id, 8.25m), createCode: false));
        var code = await In<SalesTaxService, TaxCode>(s => s.CreateCodeAsync(new TaxCodeDetails("Taxed", [rate.Id], null)));
        // 8.25 owed on a taxed sale, then paid.
        await Do<InvoiceService>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, d, [Line()], TaxCodeId: code.Id)));
        await Do<SalesTaxPaymentService>(s => s.PayAsync(new PaySalesTaxCommand(agency.Id, d.AddDays(1), checking, 8.25m)));

        // Channels: a payout file, and an order batch posted and partly returned.
        var etsy = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Etsy shop", ChannelKind.Etsy, checking)));
        await Do<ChannelService>(s => s.ImportAsync(etsy.Id, "etsy.csv", Encoding.UTF8.GetBytes(
            "Date,Type,Title,Info,Currency,Amount,Fees & Taxes,Net,Tax Details\n" +
            "\"March 2, 2026\",Sale,Payment for Order #1,Order #1,USD,$25.00,--,$25.00,--\n" +
            "\"March 2, 2026\",Fee,Transaction fee: Mug,Order #1,USD,--,-$1.63,-$1.63,--\n" +
            "\"March 9, 2026\",Deposit,\"$23.37 sent to your bank account\",,USD,--,--,-$23.37,--\n"), save: true));
        var shop = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Market stall", ChannelKind.Other, checking)));
        await Do<ChannelOrderService>(s => s.ImportAsync(shop.Id, Encoding.UTF8.GetBytes("Order ID,Sale Date,SKU,Title,Quantity,Price\nA1,2026-03-02,,Custom engraving,1,20.00\n")));
        var batch = (await In<ChannelOrderService, IReadOnlyList<ChannelOrderBatch>>(s => s.BatchesAsync(shop.Id))).Single();
        await Do<ChannelOrderService>(s => s.MapSkuAsync(shop.Id, "Custom engraving", null));
        var posted = await In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(batch.Id));
        var lineNumber = posted.Lines.Single().LineNumber;
        await Do<ChannelOrderService>(s => s.ReturnAsync(posted.Id, new DateOnly(2026, 3, 9), [(lineNumber, 1m)], "Broken", new HashSet<int> { lineNumber }));

        // Trucking: a carrier, a settlement posted, one waiting with a code that has no account, and one dismissed.
        await In<ChartTemplates, ChartImportResult>(s => s.AddTruckingAsync());
        chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        var carrier = await In<SettlementService, Carrier>(s => s.CreateCarrierAsync(new CarrierDetails("Every Kind Freight", checking, vendor.Id, Unit: "12")));
        await Do<SettlementService>(s => s.SetCodesAsync(carrier.Id, [new(SettlementSection.Revenue, "RV", Account("Freight Revenue"), "Trip revenue")]));
        SettlementDetails Statement(string number, string code) => new(number, d, 850m,
            [new(SettlementSection.Revenue, code, 1000m, d, "L1", "01", "Trip revenue", LoadedMiles: 500m, EmptyMiles: 40m), new(SettlementSection.Deduction, "UF", -150m, d, "L1", null, "Fuel", Account("Fuel"), Gallons: 40m)]);
        var settlement = await In<SettlementService, Settlement>(s => s.CreateAsync(carrier.Id, Statement("W1", "RV")));
        await Do<SettlementService>(s => s.PostAsync(settlement.Id));
        await Do<SettlementService>(s => s.CreateAsync(carrier.Id, Statement("W2", "NEW")));
        var dismissed = await In<SettlementService, Settlement>(s => s.CreateAsync(carrier.Id, Statement("W3", "RV")));
        await Do<SettlementService>(s => s.SetDismissedAsync(dismissed.Id, true));
        // One read from a scan, with a line it wasn't sure of, waiting for someone to check it.
        await Do<ISettlementStore>(async store =>
        {
            var scan = Settlement.Create(carrier.Id, Statement("W4", "RV") with { NetPaid = 0m, Lines = [new(SettlementSection.Revenue, "RV", 1000m, d, "L1", Doubt: SettlementTextParser.GuessedAmount)] }, DateTimeOffset.UtcNow, "owner");
            scan.SetReading(SettlementReadMethods.Ocr, "Made for the accessibility checks.");
            store.Add(scan);
            await store.SaveChangesAsync(default);
        });

        // Nonprofit: a gift, a pledge, a grant, a release of restricted money.
        var donor = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Pat Donor", Email: "pat@donor.test")));
        var scholarships = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Scholarships"));
        var restricted = await NewAccount("Scholarship gifts", AccountType.Income, restricted: true);
        var gift = await In<GiftService, OpenBook.Domain.Nonprofit.Donation>(s => s.RecordAsync(new RecordGiftCommand(donor.Id, d, 1000m, checking, IncomeAccountId: restricted.Id, ClassId: scholarships.Id)));
        // A household and a soft credit (C622): the household page, its statement links, and the gift's soft credit list.
        var spouse = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Sam Donor")));
        var household = await In<HouseholdService, OpenBook.Domain.Nonprofit.Household>(s => s.CreateAsync(donor.Id, "The Donor household", "Pat and Sam Donor"));
        await Do<HouseholdService>(s => s.AddMemberAsync(household.Id, spouse.Id));
        await Do<SoftCreditService>(s => s.AddAsync(gift.Id, spouse.Id, null, "Spouse"));
        await Do<GiftService>(s => s.PledgeAsync(new PledgeCommand(donor.Id, d, 500m)));
        // A gift from before the move, for the donor page's history list (C080).
        await Do<GiftHistoryService>(s => s.ImportCsvAsync($"Donor,Date,Amount,Description\r\n\"{donor.DisplayName}\",{d.AddYears(-1):yyyy-MM-dd},75.00,Old appeal\r\n"));
        await Do<GrantService>(s => s.CreateAsync(new GrantCommand("Roof repair", donor.Id, 1000m, d, d, today.AddMonths(6))));
        await Do<NonprofitService>(s => s.ReleaseAsync(new ReleaseCommand(d.AddDays(1), scholarships.Id, 300m)));

        // Pricing, projects and time.
        await Do<PricingService>(s => s.CreateLevelAsync(new PriceLevelDetails("Partners", PriceLevelKind.FixedPercent, -10m)));
        await Do<PricingService>(s => s.CreateRuleAsync(new PriceRuleDetails("Spring sale", PriceAdjustment.Percent, -15m, [service.Id])));
        await Do<ProjectService>(s => s.CreateAsync(new CreateProjectCommand(new ProjectDetails(), CustomerId: customer.Id)));
        // A fixed fee with a milestone, for the project page's milestones (C083).
        var fixedFee = await In<ProjectService, Project>(s => s.CreateAsync(new CreateProjectCommand(
            new ProjectDetails(Arrangement: BillingArrangement.FixedFee, Fee: 2000m), "Website", customer.Id)));
        await Do<ProjectMilestoneService>(s => s.AddAsync(fixedFee.Id, "Design approved", 800m, today.AddDays(14)));
        var worker = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Dana", 50m, 100m)));
        await Do<TimeService>(s => s.CreateAsync(new TimeEntryDetails(worker.Id, d, 2m, customer.Id, service.Id, true)));
        await Do<PreBillService>(s => s.MakeDraftsAsync([customer.Id], today));
        // An expense claim waiting for approval, one line billable (C345): its page, the form and the list.
        var claim = await In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(worker.Id, "Client visit",
            [new ExpenseClaimLineDraft(d, supplies, "Printer paper", 12.50m, customer.Id), new ExpenseClaimLineDraft(d, supplies, "Pens", 4m)]));

        // Audit requests this year (one overdue) and last, for the request list and its copy form (C306).
        await Do<AuditRequestService>(s => s.CreateAsync(new AuditRequestDetails(today.Year, "Bank reconciliations", "Dana", today.AddDays(-3), "All accounts")));
        await Do<AuditRequestService>(s => s.CreateAsync(new AuditRequestDetails(today.Year, "Fixed asset schedule", null, today.AddDays(10))));
        await Do<AuditRequestService>(s => s.CreateAsync(new AuditRequestDetails(today.Year - 1, "Payroll registers")));

        // Settings and lists.
        await Do<BudgetService>(s => s.CreateAsync(new CreateBudgetCommand("Next year", today.Year + 1)));
        await Do<SegmentService>(s => s.CreateAsync(SegmentKind.Location, "Main street"));
        await Do<CustomFieldService>(s => s.CreateAsync(CustomFieldTarget.Customer, new CustomFieldDetails("Pack size", CustomFieldType.Text)));
        await Do<PermissionSetService>(s => s.CreateAsync("Sales clerk", null, new Dictionary<string, UserRole> { ["sales"] = UserRole.Standard }));
        await Do<WebhookService>(s => s.CreateAsync("https://example.com/hook", null, null));
        var invite = await In<InviteService, SentInvite>(s => s.InviteAsync(new InviteCommand("sam@example.com", UserRole.Accountant, SendEmail: false), "http://localhost"));
        return ([invite.Link["http://localhost".Length..], $"/portal/{portal.Token}", $"/portal/{portal.Token}/card"],
            [$"/banking/transactions/{expense.Id}/edit", $"/nonprofit/statements/{d.Year}/{donor.Id}/email", $"/projects/{fixedFee.Id}", $"/time/claims/{claim.Id}/edit"]);
    }
}
