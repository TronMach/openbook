using System.Globalization;
using OpenBook.Domain.Common;
using OpenBook.Domain.CustomFields;

namespace OpenBook.Domain.Tests;

public sealed class CustomFieldTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static CustomField Field(CustomFieldType type, CustomFieldTarget target = CustomFieldTarget.Item, IReadOnlyList<string>? options = null,
        decimal? minimum = null, decimal? maximum = null, int? maxLength = null) =>
        CustomField.Create(target, new CustomFieldDetails("Field", type, Options: options, Minimum: minimum, Maximum: maximum, MaxLength: maxLength), Now);

    private static string Refused(Action action) => Assert.Throws<DomainException>(action).Code;

    [Fact]
    public void Values_are_checked_and_stored_in_one_form_per_kind()
    {
        Assert.Null(Field(CustomFieldType.Text).Normalize("   "));
        Assert.Equal("Case of 12", Field(CustomFieldType.Text).Normalize("  Case of 12 "));
        Assert.Equal("Line one Line two", Field(CustomFieldType.Text).Normalize("Line one\r\nLine two"));
        Assert.Equal("Line one\nLine two", Field(CustomFieldType.LongText).Normalize("Line one\r\nLine two"));
        Assert.Equal("1200", Field(CustomFieldType.WholeNumber).Normalize("1,200"));
        Assert.Equal("2.5", Field(CustomFieldType.DecimalNumber).Normalize("2.50"));
        Assert.Equal("1250.50", Field(CustomFieldType.Money).Normalize("$1,250.5"));
        Assert.Equal("2026-03-04", Field(CustomFieldType.Date).Normalize("2026-03-04"));
        Assert.Equal("Yes", Field(CustomFieldType.YesNo).Normalize("y"));
        Assert.Equal("No", Field(CustomFieldType.YesNo).Normalize("FALSE"));
        Assert.Equal("Case", Field(CustomFieldType.PickList, options: ["Each", "Case"]).Normalize("case"));
        Assert.Equal("https://example.com/p/1", Field(CustomFieldType.Link).Normalize("https://example.com/p/1"));
        Assert.Equal("rep@example.com", Field(CustomFieldType.Email).Normalize("rep@example.com"));
        Assert.Equal("+1 (555) 010-2000 x12", Field(CustomFieldType.Phone).Normalize("+1 (555) 010-2000 x12"));
    }

    [Fact]
    public void Values_that_dont_fit_are_refused()
    {
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.WholeNumber).Normalize("2.5")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.Money).Normalize("1.005")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.DecimalNumber).Normalize("lots")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.Date).Normalize("someday")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.YesNo).Normalize("maybe")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.PickList, options: ["Each"]).Normalize("Pallet")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.Link).Normalize("javascript:alert(1)")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.Email).Normalize("not an email")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.Phone).Normalize("call me")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.WholeNumber, minimum: 1, maximum: 10).Normalize("11")));
        Assert.Equal("customfield.value_invalid", Refused(() => Field(CustomFieldType.Text, maxLength: 3).Normalize("four")));
    }

    [Fact]
    public void Fields_check_their_settings()
    {
        Assert.Equal("customfield.name_required", Refused(() => CustomField.Create(CustomFieldTarget.Item, new CustomFieldDetails(" ", CustomFieldType.Text), Now)));
        Assert.Equal("customfield.options_required", Refused(() => Field(CustomFieldType.PickList, options: [])));
        Assert.Equal("customfield.option_duplicate", Refused(() => Field(CustomFieldType.PickList, options: ["Case", "case"])));
        Assert.Equal("customfield.range_invalid", Refused(() => Field(CustomFieldType.WholeNumber, minimum: 5, maximum: 1)));
        // Customers' fields can't go on purchase documents, nor vendors' on sales documents; document fields need a document.
        Assert.Equal("customfield.documents_invalid", Refused(() => CustomField.Create(CustomFieldTarget.Customer,
            new CustomFieldDetails("Rep", CustomFieldType.Text, Documents: CustomFieldDocuments.PurchaseOrder), Now)));
        Assert.Equal("customfield.documents_invalid", Refused(() => CustomField.Create(CustomFieldTarget.Vendor,
            new CustomFieldDetails("Rep", CustomFieldType.Text, Documents: CustomFieldDocuments.Invoice), Now)));
        Assert.Equal("customfield.documents_required", Refused(() => CustomField.Create(CustomFieldTarget.Document, new CustomFieldDetails("Ship via", CustomFieldType.Text), Now)));
        // The default must be a value the field accepts.
        Assert.Equal("customfield.value_invalid", Refused(() => CustomField.Create(CustomFieldTarget.Item,
            new CustomFieldDetails("Unit", CustomFieldType.PickList, Options: ["Each"], DefaultValue: "Case"), Now)));

        var unit = CustomField.Create(CustomFieldTarget.Customer, new CustomFieldDetails("Unit", CustomFieldType.PickList, Options: ["Each", "Case"],
            DefaultValue: "each", Placement: CustomFieldPlacement.Column, ShowInList: true), Now);
        Assert.Equal("Each", unit.DefaultValue);
        Assert.Equal(CustomFieldPlacement.UnderDescription, unit.Placement);
        Assert.Equal(["Each", "Case"], unit.OptionList);
    }

    [Fact]
    public void Values_display_in_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            Assert.Equal("3/4/2026", Field(CustomFieldType.Date).Display("2026-03-04"));
            Assert.Equal("1,250.50", Field(CustomFieldType.Money).Display("1250.50"));
            Assert.Equal("1,200", Field(CustomFieldType.WholeNumber).Display("1200"));
            Assert.Equal("Case of 12", Field(CustomFieldType.Text).Display("Case of 12"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Every_ready_made_field_is_valid_and_has_its_own_key_and_name()
    {
        Assert.Equal(CustomFieldPresets.All.Count, CustomFieldPresets.All.Select(p => p.Key).Distinct().Count());
        Assert.All(CustomFieldPresets.All.GroupBy(p => p.Target), g =>
            Assert.Equal(g.Count(), g.Select(p => p.Details.Name.ToUpperInvariant()).Distinct().Count()));
        foreach (var preset in CustomFieldPresets.All)
            Assert.Equal(preset.Details.Name, CustomField.Create(preset.Target, preset.Details, Now, preset.Key).Name);
        // The document fields filled from a customer's or vendor's field share its name.
        var documents = CustomFieldPresets.All.Where(p => p.Target == CustomFieldTarget.Document).Select(p => p.Details.Name).ToHashSet();
        Assert.Subset(documents, new HashSet<string> { "Sales rep", "Delivery instructions", "Contract number", "Ship via" });
    }
}
