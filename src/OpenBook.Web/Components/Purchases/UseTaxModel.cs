using OpenBook.Application.SalesTax;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Web.Components.Purchases;

/// <summary>Use tax as ticked in <see cref="UseTaxField"/>.</summary>
public sealed class UseTaxModel
{
    public bool Owed { get; set; }
    public Guid? CodeId { get; set; }

    /// <summary>Typed over the tax worked out; null for that.</summary>
    public decimal? Amount { get; set; }

    /// <summary>For a new document: null when it has none.</summary>
    public UseTaxInput? ForNew => Owed && CodeId is { } code ? new UseTaxInput(code, Amount) : null;

    /// <summary>For a correction, where an empty code takes it off.</summary>
    public UseTaxInput ForChange => ForNew ?? new UseTaxInput(Guid.Empty);

    public static UseTaxModel From(UseTax? tax) =>
        tax is null ? new UseTaxModel() : new UseTaxModel { Owed = true, CodeId = tax.TaxCodeId, Amount = tax.IsTyped ? tax.Total : null };
}
