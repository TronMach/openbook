using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenBook.QuickBooksExport;

/// <summary>A conversation with QuickBooks: one qbXML request in, one response out.</summary>
public interface IQuickBooksSession : IDisposable
{
    /// <summary>The qbXML versions this QuickBooks accepts ("13.0", "CA3.0", …).</summary>
    IReadOnlyList<string> Versions { get; }

    string Process(string request);
}

/// <summary>QuickBooks refused or couldn't answer; the message says what to do.</summary>
public sealed class QuickBooksException(string message) : Exception(message);

/// <summary>
/// QuickBooks' own request processor (QBXMLRP2.RequestProcessor, installed with QuickBooks Desktop), late-bound so
/// nothing from Intuit's SDK is needed to build or run this. The first time, QuickBooks asks its admin (signed in,
/// single-user) whether to let "OpenBook export" read the company file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RequestProcessorSession : IQuickBooksSession
{
    public const string AppName = "OpenBook export";
    private const int LocalQuickBooks = 1; // ctLocalQBD
    private const int AnyFileMode = 2; // omDontCare: single- or multi-user, whichever QuickBooks is in

    private readonly dynamic _processor;
    private readonly string _ticket;
    private bool _closed;

    /// <param name="companyFile">Null uses the company open in QuickBooks; a .QBW path asks QuickBooks to open that one.</param>
    public RequestProcessorSession(string? companyFile)
    {
        var type = Type.GetTypeFromProgID("QBXMLRP2.RequestProcessor")
            ?? throw new QuickBooksException("QuickBooks Desktop isn't installed on this computer (its request processor, QBXMLRP2, isn't registered). Run this on the PC where QuickBooks is.");
        _processor = Activator.CreateInstance(type)!;
        try
        {
            _processor.OpenConnection2("", AppName, LocalQuickBooks);
        }
        catch (COMException ex)
        {
            throw Explain(ex);
        }

        try
        {
            _ticket = _processor.BeginSession(companyFile ?? "", AnyFileMode);
            var versions = new List<string>();
            foreach (var v in (IEnumerable)_processor.QBXMLVersionsForSession(_ticket))
                versions.Add(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
            Versions = versions;
        }
        catch (COMException ex)
        {
            _processor.CloseConnection();
            throw Explain(ex);
        }
    }

    public IReadOnlyList<string> Versions { get; }

    public string Process(string request)
    {
        try
        {
            return _processor.ProcessRequest(_ticket, request);
        }
        catch (COMException ex)
        {
            throw Explain(ex);
        }
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            _processor.EndSession(_ticket);
            _processor.CloseConnection();
        }
        catch (COMException)
        {
            // QuickBooks closed first; nothing left to end.
        }
    }

    /// <summary>The request processor's usual refusals in words, always with QuickBooks' own message and code.</summary>
    private static QuickBooksException Explain(COMException ex) => new(((uint)ex.HResult switch
    {
        0x80040408 => "QuickBooks couldn't be started or reached. Start QuickBooks, open the company file, and try again.",
        0x80040416 or 0x80040417 => "No company file is open in QuickBooks. Open it (restore a .QBM or .QBB first: File › Open or Restore Company), or pass --company-file.",
        0x8004041A or 0x8004041D or 0x80040418 or 0x80040420 => "QuickBooks didn't allow OpenBook export to read this company. Sign in to QuickBooks as the admin in single-user mode, run this again and choose \"Yes, this time\" (or \"Yes, whenever this QuickBooks company file is open\") when QuickBooks asks.",
        0x80040410 => "QuickBooks has a different company file open, or the file is open in another mode. Close other company files and try again.",
        0x80040154 => "QuickBooks Desktop isn't installed on this computer. Run this on the PC where QuickBooks is.",
        _ => "QuickBooks couldn't answer.",
    }) + $" (QuickBooks: {ex.Message.Trim()} 0x{ex.HResult:X8})");
}
