using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.CustomFields;
using OpenBook.Domain.Common;
using OpenBook.Domain.CustomFields;

namespace OpenBook.Api;

public sealed record CustomFieldResponse(
    Guid Id, CustomFieldTarget Target, string Name, string? HelpText, CustomFieldType Type, IReadOnlyList<string> Options, bool Required,
    string? DefaultValue, IReadOnlyList<CustomFieldDocuments> Documents, CustomFieldPlacement Placement, bool ShowInList, bool PrintOnForms,
    int? MaxLength, decimal? Minimum, decimal? Maximum, int Position, bool IsActive, string? PresetKey)
{
    public static CustomFieldResponse From(CustomField f) => new(
        f.Id, f.Target, f.Name, f.HelpText, f.Type, f.OptionList, f.IsRequired, f.DefaultValue, [.. CustomField.Each(f.Documents)], f.Placement,
        f.ShowInList, f.PrintOnForms, f.MaxLength, f.Minimum, f.Maximum, f.Position, f.IsActive, f.PresetKey);
}

/// <param name="Target">Needed to create a field; ignored on a change (a field stays with its kind of record).</param>
/// <param name="Documents">For document fields, the documents that have it; otherwise those its value is copied onto.</param>
public sealed record CustomFieldRequest(
    string Name,
    CustomFieldType Type,
    CustomFieldTarget? Target = null,
    string? HelpText = null,
    IReadOnlyList<string>? Options = null,
    bool Required = false,
    string? DefaultValue = null,
    IReadOnlyList<CustomFieldDocuments>? Documents = null,
    CustomFieldPlacement Placement = CustomFieldPlacement.UnderDescription,
    bool ShowInList = false,
    bool PrintOnForms = true,
    int? MaxLength = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    int Position = 0)
{
    public CustomFieldDetails Details() => new(
        Name, Type, HelpText, Options, Required, DefaultValue, (Documents ?? []).Aggregate(CustomFieldDocuments.None, (all, d) => all | d), Placement,
        ShowInList, PrintOnForms, MaxLength, Minimum, Maximum, Position);
}

public sealed record CustomFieldPresetResponse(string Key, CustomFieldTarget Target, string Name, CustomFieldType Type, string? HelpText);

public sealed record AddCustomFieldPresetsRequest(IReadOnlyList<string> Keys);

/// <param name="Value">As stored: numbers with "." decimals, dates yyyy-mm-dd, yes/no as "Yes"/"No".</param>
/// <param name="Display">As people read it.</param>
public sealed record CustomFieldValueResponse(Guid FieldId, string Name, CustomFieldType Type, string? Value, string? Display, bool IsActive);

internal static class CustomFieldsApi
{
    public static void MapCustomFieldsApi(this RouteGroupBuilder api)
    {
        var fields = api.MapGroup("/settings/custom-fields");
        fields.MapGet("/", async (CustomFieldTarget? target, CustomFieldService s, CancellationToken ct) =>
            (await s.ListAsync(target, ct: ct)).Select(CustomFieldResponse.From));
        fields.MapGet("/{id:guid}", async (Guid id, CustomFieldService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } f ? Results.Ok(CustomFieldResponse.From(f)) : Results.NotFound());
        fields.MapPost("/", async (CustomFieldRequest request, CustomFieldService s, CancellationToken ct) =>
        {
            var target = request.Target ?? throw new DomainException("customfield.target_required", "Say what the field belongs to: Item, Customer, Vendor or Document.");
            var field = await s.CreateAsync(target, request.Details(), ct);
            return Results.Created($"/api/settings/custom-fields/{field.Id}", CustomFieldResponse.From(field));
        });
        fields.MapPut("/{id:guid}", async (Guid id, CustomFieldRequest request, CustomFieldService s, CancellationToken ct) =>
            CustomFieldResponse.From(await s.UpdateAsync(id, request.Details(), ct)));
        fields.MapPost("/{id:guid}/deactivate", async (Guid id, CustomFieldService s, CancellationToken ct) =>
            CustomFieldResponse.From(await s.SetActiveAsync(id, false, ct)));
        fields.MapPost("/{id:guid}/reactivate", async (Guid id, CustomFieldService s, CancellationToken ct) =>
            CustomFieldResponse.From(await s.SetActiveAsync(id, true, ct)));

        // The ready-made fields not added yet, and adding some by key.
        fields.MapGet("/presets", async (CustomFieldService s, CancellationToken ct) =>
            (await s.AvailablePresetsAsync(ct)).Select(p => new CustomFieldPresetResponse(p.Key, p.Target, p.Details.Name, p.Details.Type, p.Details.HelpText)));
        fields.MapPost("/presets", async (AddCustomFieldPresetsRequest request, CustomFieldService s, CancellationToken ct) =>
            (await s.AddPresetsAsync(request.Keys ?? [], ct)).Select(CustomFieldResponse.From));

        // A record's values: entityType is item, customer, vendor, invoice, estimate, sales_order, sales_receipt,
        // credit_memo, purchase_order or bill. PUT takes { "field id or name": "value" }; null or "" clears, others are kept.
        var values = api.MapGroup("/custom-fields");
        values.MapGet("/{entityType}/{id:guid}", async (string entityType, Guid id, CustomFieldService s, CancellationToken ct) =>
            Values(await s.EntriesAsync(entityType, id, ct)));
        values.MapGet("/{entityType}/{id:guid}/lines", async (string entityType, Guid id, CustomFieldService s, CancellationToken ct) =>
        {
            if (!CustomFieldService.EntityTypes.ContainsKey(entityType))
                throw new DomainException("customfield.entity_type", $"Custom fields can't go on '{entityType}'.");
            return (await s.LineEntriesAsync(id, ct)).OrderBy(l => l.Key).Select(l => new { Line = l.Key, Fields = Values(l.Value) });
        });
        values.MapPut("/{entityType}/{id:guid}", async (string entityType, Guid id, Dictionary<string, string?> body, CustomFieldService s, CancellationToken ct) =>
        {
            var shown = await s.EntriesAsync(entityType, id, ct);
            var byKey = body.ToDictionary(
                kv => Guid.TryParse(kv.Key, out var fieldId) ? fieldId
                    : shown.FirstOrDefault(e => string.Equals(e.Field.Name, kv.Key, StringComparison.OrdinalIgnoreCase))?.Field.Id
                      ?? throw new DomainException("customfield.not_on_record", $"There's no field \"{kv.Key}\" on this record."),
                kv => kv.Value);
            return Values(await s.SaveAsync(entityType, id, byKey, ct));
        });
    }

    private static IEnumerable<CustomFieldValueResponse> Values(IEnumerable<CustomFieldEntry> entries) =>
        entries.Select(e => new CustomFieldValueResponse(e.Field.Id, e.Field.Name, e.Field.Type, e.Value, e.Display, e.Field.IsActive));
}
