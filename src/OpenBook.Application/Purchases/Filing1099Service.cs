using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <summary>What happened with a year's copies: sent, and the vendors with no email address or whose send failed.</summary>
public sealed record Copies1099Run(int Sent, IReadOnlyList<string> NoEmail, IReadOnlyList<string> Failed);

/// <summary>
/// The work around filing 1099s that isn't the filing itself: the copies vendors are owed, asking for a W-9, and a note
/// of what was filed. OpenBook doesn't file with the IRS.
/// </summary>
public sealed class Filing1099Service(
    IPurchasesStore store,
    ICompanyStore companies,
    PayablesService payables,
    FormDesignService designs,
    IFormRenderer renderer,
    EmailService email,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "filing_1099";

    /// <summary>Vendors over the threshold for the year, with what's been filed for each.</summary>
    public async Task<IReadOnlyDictionary<Guid, Filing1099>> FiledAsync(int year, CancellationToken ct = default) =>
        (await store.ListFilings1099Async(year, ct)).ToDictionary(f => f.VendorId);

    /// <summary>Who needs a 1099 for the year and has no tax ID on file.</summary>
    public async Task<IReadOnlyList<Vendor>> MissingTaxIdsAsync(int year, CancellationToken ct = default) =>
        (await payables.Report1099Async(year, null, ct)).Vendors.Where(v => v.MeetsThreshold && v.Vendor.TaxId is null).Select(v => v.Vendor).ToList();

    /// <summary>One vendor's copy, or all of them for the year in one PDF to print.</summary>
    public async Task<RenderedForm> CopiesAsync(int year, decimal? threshold = null, Guid? vendorId = null, CancellationToken ct = default)
    {
        var due = await DueAsync(year, threshold, ct);
        if (vendorId is { } id)
            due = due.Where(v => v.Vendor.Id == id).ToList();
        if (due.Count == 0)
            throw new DomainException("filing1099.none_due", $"No vendor needs a 1099 for {year}.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var style = await designs.DefaultStyleAsync(ct);
        var letters = due.Select(v => Letter(company, v, year, style)).ToList();
        var name = due.Count == 1 ? due[0].Vendor.DisplayName : string.Empty;
        return new RenderedForm(FormService.FileName(company, $"1099 {year}", name), renderer.Render(letters), Guid.Empty, $"1099 copies {year}");
    }

    /// <summary>Emails each vendor due a copy that has an address. Each is saved on its own; a failure doesn't stop the rest.</summary>
    public async Task<Copies1099Run> EmailCopiesAsync(int year, decimal? threshold = null, CancellationToken ct = default)
    {
        var due = await DueAsync(year, threshold, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var filed = await FiledAsync(year, ct);
        List<string> noEmail = [], failed = [];
        var sent = 0;
        foreach (var vendor in due)
        {
            if (vendor.Vendor.Email is not { } address)
            {
                noEmail.Add(vendor.Vendor.DisplayName);
                continue;
            }

            try
            {
                var copy = await CopiesAsync(year, threshold, vendor.Vendor.Id, ct);
                await email.SendWordedAsync(EmailKind.Form1099Copy, address, Values(company, vendor.Vendor, year),
                    [new EmailAttachment(copy.FileName, "application/pdf", copy.Pdf)], null, "vendor", vendor.Vendor.Id, ct);
                store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Emailed, "vendor", vendor.Vendor.Id, null,
                    JsonSerializer.Serialize(new { Emailed = "1099 copy", Year = year })));
                if (filed.GetValueOrDefault(vendor.Vendor.Id) is { } filing)
                    (await store.GetFiling1099Async(filing.Id, ct))?.MarkCopySent(clock.UtcNow);
                await store.SaveChangesAsync(ct);
                sent++;
            }
            catch (DomainException ex) when (ex.Code != "email.not_configured")
            {
                failed.Add($"{vendor.Vendor.DisplayName}: {ex.Message}");
            }
        }

        return new Copies1099Run(sent, noEmail, failed);
    }

    /// <summary>Asks a vendor for a W-9 by email, with the IRS's form linked.</summary>
    public async Task RequestW9Async(Guid vendorId, CancellationToken ct = default)
    {
        var vendor = await store.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
        var address = vendor.Email ?? throw new DomainException("filing1099.no_email", $"{vendor.DisplayName} has no email address.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        await email.SendWordedAsync(EmailKind.W9Request, address, Values(company, vendor, CompanyTime.Today.Year), [], null, "vendor", vendor.Id, ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Emailed, "vendor", vendor.Id, null,
            JsonSerializer.Serialize(new { Emailed = "W-9 request" })));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Notes that the year's forms were filed: a row for each vendor due one that hasn't one.</summary>
    public async Task<int> MarkFiledAsync(int year, DateOnly filedOn, decimal? threshold = null, CancellationToken ct = default)
    {
        var due = await DueAsync(year, threshold, ct);
        var filed = await FiledAsync(year, ct);
        var added = 0;
        foreach (var vendor in due.Where(v => !filed.ContainsKey(v.Vendor.Id)))
        {
            store.Add(Filing1099.Create(year, vendor.Vendor.Id, vendor.Vendor.Form1099Box, vendor.Total, filedOn, user.Actor));
            added++;
        }

        if (added > 0)
        {
            var company = await companies.GetRequiredCompanyAsync(ct);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, company.Id, null,
                JsonSerializer.Serialize(new { Year = year, FiledOn = filedOn, Forms = added })));
            await store.SaveChangesAsync(ct);
        }

        return added;
    }

    /// <summary>Takes back a year's note of filing, marked by mistake or to be marked again.</summary>
    public async Task ClearFiledAsync(int year, CancellationToken ct = default)
    {
        var filed = await store.ListFilings1099Async(year, ct);
        if (filed.Count == 0)
            return;
        var company = await companies.GetRequiredCompanyAsync(ct);
        foreach (var filing in filed)
            store.Remove(filing);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, company.Id,
            JsonSerializer.Serialize(new { Year = year, Forms = filed.Count }), null));
        await store.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<Vendor1099>> DueAsync(int year, decimal? threshold, CancellationToken ct) =>
        (await payables.Report1099Async(year, threshold, ct)).Vendors.Where(v => v.MeetsThreshold).OrderBy(v => v.Vendor.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    private static Dictionary<string, string> Values(Company company, Vendor vendor, int year) => new()
    {
        ["vendor"] = vendor.DisplayName,
        ["year"] = year.ToString(CultureInfo.InvariantCulture),
        ["company"] = company.LegalName ?? company.Name,
    };

    // A statement to the recipient, in place of the IRS's Copy B: the same figures, with their number masked.
    private static DonorLetter Letter(Company company, Vendor1099 due, int year, FormStyle style)
    {
        var vendor = due.Vendor;
        var (form, box) = AccountantPackageService.FormAndBox(vendor.Form1099Box);
        List<string> paragraphs =
        [
            $"Payer: {company.LegalName ?? company.Name}. Payer's TIN: {company.TaxId ?? "not on file"}.",
            $"Recipient: {vendor.CompanyName ?? vendor.DisplayName}. Recipient's TIN: {vendor.MaskedTaxId ?? "not on file"}.",
            "This is important tax information and is being furnished to the IRS. If you're required to file a return, a penalty may be imposed on you if this income is taxable and the IRS determines that it hasn't been reported.",
        ];
        return new DonorLetter(
            new FormParty(company.LegalName ?? company.Name, company.Address, company.Email, company.Phone),
            new FormParty(vendor.CompanyName ?? vendor.DisplayName, vendor.Address, vendor.Email, vendor.Phone),
            $"Form {form} recipient statement {year}",
            new DateOnly(year, 12, 31),
            $"Here is what we paid you in {year}, as reported on Form {form}.",
            [new DonorLetterGift(new DateOnly(year, 12, 31), $"Box {box}: {AccountantPackageService.BoxName(vendor.Form1099Box)}", due.Total)],
            paragraphs,
            null,
            null,
            company.HomeCurrency.Code,
            style);
    }
}
