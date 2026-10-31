using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <summary>Purchase orders as PDF (the company's form style) and emailed to the vendor.</summary>
public sealed class PurchaseOrderForms(
    IPurchasesStore store, ICompanyStore companies, IFormRenderer renderer, EmailService email, CustomFields.CustomFieldService customFields, FormDesignService designs, IClock clock, ICurrentUser user)
{
    public async Task<RenderedForm> RenderAsync(Guid id, CancellationToken ct = default)
    {
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(PurchaseOrderService.EntityType, id);
        var vendor = await store.GetVendorAsync(order.VendorId, ct) ?? throw new EntityNotFoundException("vendor", order.VendorId);
        var company = await companies.GetRequiredCompanyAsync(ct);

        var facts = new List<(string, string)> { ("Date", D(order.Date)) };
        if (order.ExpectedDate is { } expected)
            facts.Add(("Expected", D(expected)));
        if (vendor.AccountNumber is { } account)
            facts.Add(("Our account", account));
        var lines = order.Lines.Select(l => l.IsItem
            ? new FormLine(l.Description, l.Quantity, l.UnitCost, l.Amount, LineNumber: l.LineNumber)
            : new FormLine(l.Description, null, null, l.Amount, LineNumber: l.LineNumber)).ToList();
        var party = new FormParty(vendor.CompanyName is { } name && name != vendor.DisplayName ? $"{vendor.DisplayName}\n{name}" : vendor.DisplayName,
            vendor.Address, vendor.Email, vendor.Phone);
        var form = new SalesForm("Purchase order", order.Number, FormService.CompanyParty(company), party, facts, lines,
            [new FormTotal("Total", order.Total, Emphasis: true)], order.Memo, company.HomeCurrency.Code, FormService.Unstyled, PartyLabel: "VENDOR");
        form = FormDesignService.Apply(form, await designs.ForAsync(FormDocuments.PurchaseOrder, ct: ct), FormDocuments.PurchaseOrder);
        form = await customFields.DecorateAsync(form, order.Id, ct);
        return new RenderedForm(FormService.FileName(company, form.Title, order.Number), renderer.Render(form), Guid.Empty, $"{form.Title} {order.Number}");
    }

    public async Task<EmailDraft> DraftAsync(Guid id, CancellationToken ct = default)
    {
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(PurchaseOrderService.EntityType, id);
        var vendor = await store.GetVendorAsync(order.VendorId, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var expected = order.ExpectedDate is { } date ? $" We'd like it by {D(date)}." : "";
        return new EmailDraft(vendor?.Email ?? "", $"Purchase order {order.Number} from {company.Name}",
            $"Hello,\n\nPlease find our purchase order {order.Number} attached.{expected}\n\nThank you,\n{company.Name}");
    }

    /// <summary>Emails the PDF and records the send on the order.</summary>
    public async Task SendAsync(Guid id, EmailDraft message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(PurchaseOrderService.EntityType, id);
        if (order.Status == PurchaseOrderStatus.Cancelled)
            throw new DomainException("purchase_order.cancelled", "This order is cancelled.");
        var rendered = await RenderAsync(id, ct);
        var to = Domain.Companies.EmailSettings.Address(message.To, "To") ?? throw new DomainException("email.to_required", "Enter the vendor's email address.");
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? rendered.Title : message.Subject.Trim();
        await email.SendMessageAsync(new EmailMessage(to, subject, message.Body ?? "", [new EmailAttachment(rendered.FileName, "application/pdf", rendered.Pdf)]), ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Emailed, PurchaseOrderService.EntityType, id, null,
            JsonSerializer.Serialize(new { To = to, Form = rendered.Title })));
        await store.SaveChangesAsync(ct);
    }

    private static string D(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);
}
