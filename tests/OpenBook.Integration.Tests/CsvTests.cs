using OpenBook.Application.Import;
using OpenBook.Domain.Common;

namespace OpenBook.Integration.Tests;

public class CsvTests
{
    private static IReadOnlyList<string> Fields(IReadOnlyList<CsvRecord> records, int index) => records[index].Fields;

    [Fact]
    public void Parses_quotes_doubled_quotes_and_embedded_delimiters()
    {
        var records = Csv.Parse("Name,Description\r\n\"Rent, office\",\"The \"\"big\"\" one\"\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(["Rent, office", "The \"big\" one"], Fields(records, 1));
    }

    [Fact]
    public void Line_breaks_inside_quotes_stay_in_the_field_and_line_numbers_follow()
    {
        var records = Csv.Parse("A,B\n\"one\ntwo\",x\nlast,y\n");

        Assert.Equal("one\ntwo", Fields(records, 1)[0]);
        Assert.Equal(2, records[1].Line);
        Assert.Equal(4, records[2].Line);
    }

    [Fact]
    public void Skips_blank_lines_strips_bom_and_trims_unquoted_values()
    {
        var records = Csv.Parse("﻿Name , Type\n\n  Rent ,  Expense  \n\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(["Name", "Type"], Fields(records, 0));
        Assert.Equal(["Rent", "Expense"], Fields(records, 1));
        Assert.Equal(3, records[1].Line);
    }

    [Theory]
    [InlineData("Name;Type\nRent;Expense")]
    [InlineData("Name\tType\nRent\tExpense")]
    public void Detects_semicolon_and_tab_delimiters(string text) =>
        Assert.Equal(["Rent", "Expense"], Fields(Csv.Parse(text), 1));

    [Fact]
    public void Keeps_empty_trailing_fields()
    {
        var records = Csv.Parse("A,B,C\nx,,\n");
        Assert.Equal(["x", "", ""], Fields(records, 1));
    }

    [Fact]
    public void Unterminated_quote_is_an_error() =>
        Assert.Equal("csv.unterminated_quote", Assert.Throws<DomainException>(() => Csv.Parse("A,B\n\"open,x\n")).Code);

    [Theory]
    [InlineData("Accnt. #", "accnt")]
    [InlineData("Account Type", "accounttype")]
    [InlineData("Accounts Receivable (A/R)", "accountsreceivablear")]
    public void Normalize_keeps_only_letters_and_digits(string input, string expected) =>
        Assert.Equal(expected, Csv.Normalize(input));
}
