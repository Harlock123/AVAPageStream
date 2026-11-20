using System.Diagnostics;
using AVAPageStream.Core;

namespace AVAPageStream.Mcp;

/// <summary>
/// What the server needs from the program without depending on its window: measuring and setting text, drawing a
/// page, exporting, and what preflight needs to know about pictures and fonts. The program (<c>AVAPageStream</c>)
/// supplies the real one, built on the same renderer as the screen; everything here is called on the host's thread.
/// </summary>
public interface IPageLibrary
{
    /// <summary>How text is measured, as the page view and export measure it.</summary>
    ITextMeasurer Measurer { get; }

    /// <summary>The fonts that can be used, by family name.</summary>
    IReadOnlyList<string> Fonts { get; }

    /// <summary>A story as it is set through its frames, or null when it has none.</summary>
    TextLayout? LayoutOf(Document doc, string storyId);

    /// <summary>A sheet (a page, or a master) drawn as it prints, as a PNG at <paramref name="dpi"/>.</summary>
    byte[] Picture(Document doc, SheetRef sheet, double dpi);

    /// <summary>A picture's natural size in points (an SVG's, an EPS's or a PDF page's own size), or null if it can't be read.</summary>
    (double Width, double Height)? PictureSize(byte[] bytes);

    /// <summary>Writes the document (or <paramref name="pages"/>, 0-based) as a PDF, as File › Export does.</summary>
    Task ExportPdf(Document doc, string path, IReadOnlyList<int>? pages, bool marks, bool cmyk, bool pdfx);

    /// <summary>What would go wrong in print, as File › Preflight finds it.</summary>
    IReadOnlyList<Issue> Preflight(Document doc, string? folder);
}

/// <summary>
/// The document an agent works on, and what goes with it: its editor (the document and its undo history), its file,
/// and adopting another. Used on the host's thread only (<see cref="IPageHost.RunAsync{T}"/>).
/// </summary>
public interface IPageSession
{
    /// <summary>The open document's editor. Every change goes through it, so the person's Ctrl+Z undoes it.</summary>
    Editor Editor { get; }

    /// <summary>The file the document was opened from or saved to, or null.</summary>
    string? FilePath { get; }

    /// <summary>The page the person is looking at (0-based), or null when no one is.</summary>
    int? CurrentPage { get; }

    /// <summary>Adopts a new document (new, open): a fresh history, and not changed.</summary>
    void Replace(Document doc, string? path);

    /// <summary>The document was written to <paramref name="path"/>, which becomes its file.</summary>
    void Saved(string path);

    /// <summary>Shows a page (the window turns to it after an agent works on it); nothing without a window.</summary>
    void Show(SheetRef sheet);
}

/// <summary>
/// Where the MCP tools' work runs. The handlers (<see cref="PageTools"/>) only touch the document through
/// <see cref="RunAsync{T}"/>, so the same handlers serve the headless server (a document of its own) and the live
/// program (the open window's document), both on the thread Avalonia's text and drawing run on.
/// </summary>
public interface IPageHost
{
    /// <summary>The folder relative paths start from; open, save, export and pictures stay inside it.</summary>
    string Root { get; }

    /// <summary>Text, drawing, export and preflight.</summary>
    IPageLibrary Library { get; }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread that owns the document, one call at a time, and returns what it
    /// returns (or throws what it throws). <paramref name="action"/> names it for the status bar.
    /// </summary>
    Task<T> RunAsync<T>(string action, Func<IPageSession, Task<T>> work, CancellationToken cancel = default);
}

public static class PageHostExtensions
{
    /// <summary>Runs work that finishes at once on the host's thread.</summary>
    public static Task<T> RunAsync<T>(this IPageHost host, string action, Func<IPageSession, T> work, CancellationToken cancel = default) =>
        host.RunAsync(action, s => Task.FromResult(work(s)), cancel);
}

/// <summary>Runs work on a UI thread (the program's Avalonia dispatcher, or a test's).</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>The program as the host sees it: the document, whether it can take a call now, and the status bar.</summary>
public interface IPageDesk
{
    /// <summary>The window's document (an outside agent gets the window used last).</summary>
    IPageSession Session { get; }

    /// <summary>
    /// Why the program can't take a call just now ("a dialog box is open", "the person is dragging"), or null when it
    /// can. The person's work in progress isn't interrupted: the call waits for it.
    /// </summary>
    string? Busy { get; }

    /// <summary>Tells the person what an agent just did (the status bar).</summary>
    void Report(string text);
}

/// <summary>
/// The host: an agent's calls run against the desk's document on its UI thread, one at a time, so the page redraws
/// and Ctrl+Z undoes them like the person's own edits. While the person is in the middle of something a call waits for
/// up to <see cref="BusyWait"/>, then fails saying why.
/// </summary>
public sealed class PageHost(IUiThread ui, IPageDesk desk, IPageLibrary library, string root) : IPageHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Root { get; } = Path.GetFullPath(root);

    public IPageLibrary Library { get; } = library;

    /// <summary>How long a call waits for the person to finish what they're doing.</summary>
    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often a waiting call looks again.</summary>
    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public async Task<T> RunAsync<T>(string action, Func<IPageSession, Task<T>> work, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (ran, result, busy) = await ui.InvokeAsync(async () =>
                {
                    if (desk.Busy is { } why) return (false, default(T), why);
                    try
                    {
                        var r = await work(desk.Session);
                        desk.Report($"AI agent: {action}.");
                        return (true, r, (string?)null);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        desk.Report($"AI agent: {action} failed: {e.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                if (ran) return result!;
                if (clock.Elapsed >= BusyWait)
                    throw new PageBusyException($"AVAPageStream is busy: {busy}. The call waited {BusyWait.TotalSeconds:0} seconds; ask the person to finish, then try again.");
                await Task.Delay(BusyPoll, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The program stayed busy with the person's own work for too long to take a call.</summary>
public sealed class PageBusyException(string message) : Exception(message);

/// <summary>A document with no window round it, as <c>AVAPageStream --mcp</c> serves it.</summary>
public sealed class HeadlessSession : IPageSession
{
    public Editor Editor { get; private set; } = new(Document.New());

    public string? FilePath { get; private set; }

    public int? CurrentPage => null;

    public void Replace(Document doc, string? path)
    {
        Editor = new Editor(doc);
        FilePath = path;
    }

    public void Saved(string path)
    {
        FilePath = path;
        Editor.MarkSaved();
    }

    public void Show(SheetRef sheet) { }
}

/// <summary>The desk with no window: never busy, and it reports to no one (or to a log).</summary>
public sealed class HeadlessDesk(TextWriter? log = null) : IPageDesk
{
    public IPageSession Session { get; } = new HeadlessSession();

    public string? Busy => null;

    public void Report(string text) => log?.WriteLine(text);
}
