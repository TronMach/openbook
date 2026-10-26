using OpenBook.Domain.Common;

namespace OpenBook.Domain.Banking;

public enum PositivePayDateFormat
{
    /// <summary>10/26/2026</summary>
    MonthDayYear,

    /// <summary>2026-10-26</summary>
    YearMonthDay,

    /// <summary>10262026</summary>
    MonthDayYearCompact,
}

/// <summary>
/// How a bank account's positive pay file is laid out (remembered from the last export) and how far it has been sent.
/// Positive pay: the bank only honours checks that appear in an issue file the business sends it.
/// </summary>
public sealed class PositivePaySettings
{
    private PositivePaySettings() { }

    public Guid AccountId { get; private set; }

    /// <summary>The account number at the bank, as the bank wants it in the file.</summary>
    public string? BankAccountNumber { get; private set; }

    public PositivePayDateFormat DateFormat { get; private set; }
    public bool IncludeHeader { get; private set; } = true;
    public DateTimeOffset? LastExportedAt { get; private set; }

    /// <summary>The last date covered by an export, so the next one can start the day after.</summary>
    public DateOnly? LastExportedThrough { get; private set; }

    public static PositivePaySettings For(Guid accountId) => new() { AccountId = accountId };

    public void Update(string? bankAccountNumber, PositivePayDateFormat dateFormat, bool includeHeader)
    {
        if (!Enum.IsDefined(dateFormat))
            throw new DomainException("positive_pay.format_invalid", "Unknown date format.");
        var number = Text.Optional(bankAccountNumber, 34, "positive_pay.account_number_too_long", "Bank account number");
        if (number is not null && !number.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ' '))
            throw new DomainException("positive_pay.account_number_invalid", "Use only letters, digits, spaces and dashes in the bank account number.");
        BankAccountNumber = number;
        DateFormat = dateFormat;
        IncludeHeader = includeHeader;
    }

    public void Exported(DateTimeOffset now, DateOnly through)
    {
        LastExportedAt = now;
        if (LastExportedThrough is null || through > LastExportedThrough)
            LastExportedThrough = through;
    }
}
