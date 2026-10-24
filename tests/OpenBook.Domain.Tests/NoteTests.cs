using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Notes;

namespace OpenBook.Domain.Tests;

public class NoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Notes_are_trimmed_and_limited()
    {
        var note = Note.Create("invoice", Guid.NewGuid(), "  Call before delivery.\r\nGate code 1234. ", Now, "owner");
        Assert.Equal("Call before delivery.\nGate code 1234.", note.Text);
        Assert.Equal("note.empty", Assert.Throws<DomainException>(() => Note.Create("invoice", Guid.NewGuid(), " ", Now, "owner")).Code);
        Assert.Equal("note.too_long", Assert.Throws<DomainException>(() => Note.Create("invoice", Guid.NewGuid(), new string('x', Note.MaxLength + 1), Now, "owner")).Code);
    }

    [Fact]
    public void Imported_entries_are_journal_entries() => Assert.True(JournalEntry.IsJournalSource(JournalEntry.ImportSource));
}
