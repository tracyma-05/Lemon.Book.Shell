namespace Lemon.Book.Shell.Sync;

/// <summary>
/// The sync server (the learning API the book saves to when the child is signed in). Like the update feed it is never
/// written in this repository: it is built in at build time (<c>dotnet publish -p:SyncServer=https://…/</c>, or a
/// git-ignored <c>sync.local.props</c>, see Directory.Build.props), or set by <see cref="FileName"/> next to the
/// program. Without one, sign-in is not offered and everything stays on the device.
/// The server tells the book which sign-in service and client to use (<see cref="SyncClientInfo"/>).
/// </summary>
public static class SyncServer
{
    public const string FileName = "sync-server.txt";

    /// <summary>The book's name on the server (<see cref="BookShell.SyncApp"/>).</summary>
    public static string App => BookShell.SyncApp;

    /// <summary>The address built into this release (the book's <c>SyncServer</c> metadata), or null.</summary>
    public static string? BuiltIn => BookShell.Metadata("SyncServer");

    /// <summary>The server's base address (ending in /), or null when there is none.</summary>
    public static Uri? Resolve(string? installFolder = null)
    {
        var text = ReadInstallFolder(installFolder ?? AppContext.BaseDirectory) ?? BuiltIn;
        return Parse(text);
    }

    internal static Uri? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.EndsWith('/')) text += "/";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? uri
            : null;
    }

    /// <summary>The first line of <see cref="FileName"/> that is not empty or a <c>#</c> comment.</summary>
    internal static string? ReadInstallFolder(string folder)
    {
        if (OperatingSystem.IsBrowser()) return null;
        try
        {
            var path = Path.Combine(folder, FileName);
            return File.Exists(path)
                ? File.ReadLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
