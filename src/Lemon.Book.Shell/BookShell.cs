using System.Reflection;
using Lemon.Book.Shell.Sync;

namespace Lemon.Book.Shell;

/// <summary>
/// What the shell needs to know about the book it runs: set once by the book at start (and by its tests) with
/// <see cref="Configure"/>, before anything loads its settings.
/// </summary>
public static class BookShell
{
    private static BookShellOptions s_options = new();

    public static BookShellOptions Options => s_options;

    /// <summary>
    /// The book's own assembly: its build metadata (<c>UpdateFeed</c>, <c>SyncServer</c>), its strings and its
    /// narration manifest (the embedded resource <c>&lt;assembly name&gt;.Narration.Manifest</c>).
    /// </summary>
    public static Assembly AppAssembly => s_options.AppAssembly ?? Assembly.GetEntryAssembly() ?? typeof(BookShell).Assembly;

    /// <summary>The book's name on the sync server (<c>api/app/learning/&lt;name&gt;/…</c>).</summary>
    public static string SyncApp => s_options.SyncApp;

    /// <summary>How many pages the book has: bookmarks and notes on a page past the end are dropped.</summary>
    public static int PageCount => s_options.PageCount?.Invoke() ?? int.MaxValue;

    /// <summary>The book's own sync rules, for its own kinds of progress.</summary>
    public static ISyncRules SyncRules => s_options.SyncRules ?? Sync.SyncRules.None;

    public static void Configure(BookShellOptions options) => s_options = options;

    /// <summary>A build metadata value of the book (an <c>AssemblyMetadata</c> item in its project), or null.</summary>
    public static string? Metadata(string key)
    {
        var value = AppAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <param name="AppAssembly">The book's assembly; the entry assembly when left out.</param>
/// <param name="SyncApp">The book's name on the sync server.</param>
/// <param name="PageCount">How many pages the book has now.</param>
/// <param name="SyncRules">How the book's own progress merges and what it records for the account.</param>
public sealed record BookShellOptions(
    Assembly? AppAssembly = null,
    string SyncApp = "book",
    Func<int>? PageCount = null,
    ISyncRules? SyncRules = null)
{
    /// <summary>The cultures the book is written in, the first the default: zh-CN alone for a Chinese-only book.</summary>
    public IReadOnlyList<string> Cultures { get; init; } = ["zh-CN"];
}
