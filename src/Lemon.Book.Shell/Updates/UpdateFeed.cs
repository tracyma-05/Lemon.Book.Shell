using System.Reflection;
using System.Text.RegularExpressions;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Updates;

/// <summary>Where updates are read from: a GitHub repository's latest release, or a JSON manifest at an address.</summary>
/// <param name="Address">The GitHub API's "latest release" endpoint, or the manifest's address.</param>
/// <param name="IsGitHub">Read <paramref name="Address"/> as a GitHub release rather than a manifest.</param>
/// <param name="Text">The feed as written, e.g. <c>github:owner/repo</c>, for the settings.</param>
public sealed record UpdateSource(Uri Address, bool IsGitHub, string Text);

/// <summary>Where the setting came from, for the settings to say.</summary>
public enum UpdateFeedOrigin
{
    /// <summary>Built into this release (the repository it was published from).</summary>
    BuiltIn,

    /// <summary><c>update-feed.txt</c> next to the program: set by whoever packaged this copy.</summary>
    InstallFolder,

    /// <summary>The reader's own address, in the settings.</summary>
    Reader,
}

/// <summary>
/// The update address. Anyone can point the book's updates at their own server, at three levels, each overriding
/// the one before:
/// <list type="number">
/// <item>Built in at build time: <c>dotnet publish -p:UpdateFeed=…</c>. The release workflow sets it to the
/// repository it publishes from (<c>github:owner/repo</c>), so a fork that publishes releases updates from itself.</item>
/// <item><see cref="FileName"/> next to the program, for a copy repackaged without rebuilding.</item>
/// <item>The reader's own address in the settings (<see cref="SettingKey"/>).</item>
/// </list>
/// A feed is <c>github:owner/repo</c> (or the repository's https://github.com/owner/repo address) for its latest
/// GitHub Release, any other http(s) address of a JSON manifest (see <see cref="UpdateManifest"/>), or <c>off</c>.
/// </summary>
public static partial class UpdateFeed
{
    public const string FileName = "update-feed.txt";
    public const string SettingKey = "update.feed";
    public const string CheckOnStartupSetting = "update.checkOnStartup";
    public const string Off = "off";

    private const string GitHubPrefix = "github:";

    /// <summary>The address built into this release, or null (a local build).</summary>
    public static string? BuiltIn { get; } = ReadBuiltIn();

    /// <summary>The feed in force and where it came from; a null feed means updates are off.</summary>
    public static (string? Feed, UpdateFeedOrigin Origin) Resolve(ISettingsStore settings, string? installFolder = null)
    {
        if (settings.Get(SettingKey) is { } reader && !string.IsNullOrWhiteSpace(reader))
        {
            return (reader.Trim(), UpdateFeedOrigin.Reader);
        }

        if (ReadInstallFolder(installFolder ?? AppContext.BaseDirectory) is { } packaged)
        {
            return (packaged, UpdateFeedOrigin.InstallFolder);
        }

        return (BuiltIn, UpdateFeedOrigin.BuiltIn);
    }

    /// <summary>The address to read for <paramref name="feed"/>, or null when it is off, empty or not understood.</summary>
    public static UpdateSource? Parse(string? feed)
    {
        if (string.IsNullOrWhiteSpace(feed))
        {
            return null;
        }

        var text = feed.Trim();
        if (string.Equals(text, Off, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var repository = text.StartsWith(GitHubPrefix, StringComparison.OrdinalIgnoreCase) ? text[GitHubPrefix.Length..].Trim()
            : text.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ? text["https://github.com/".Length..]
            : null;
        if (repository is not null)
        {
            return TryParseGitHubRepository(repository, out var owner, out var repo)
                ? new UpdateSource(new Uri($"https://api.github.com/repos/{owner}/{repo}/releases/latest"), true, text)
                : null;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? new UpdateSource(uri, false, text)
            : null;
    }

    /// <summary>True when <paramref name="feed"/> is <c>off</c>, empty, or an address <see cref="Parse"/> reads.</summary>
    public static bool IsValid(string? feed) =>
        string.IsNullOrWhiteSpace(feed) || string.Equals(feed.Trim(), Off, StringComparison.OrdinalIgnoreCase) || Parse(feed) is not null;

    /// <summary>The first line of <see cref="FileName"/> that is not empty or a <c>#</c> comment.</summary>
    internal static string? ReadInstallFolder(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return File.ReadLines(path)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool TryParseGitHubRepository(string? value, out string owner, out string repo)
    {
        owner = repo = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        var match = GitHubRepositoryPattern().Match(text);
        if (!match.Success || match.Groups["repo"].Value is "." or "..")
        {
            return false;
        }

        owner = match.Groups["owner"].Value;
        repo = match.Groups["repo"].Value;
        return true;
    }

    // The book's own build metadata: the package is the same for every book.
    private static string? ReadBuiltIn() => BookShell.Metadata("UpdateFeed");

    // GitHub's own rules: owners are alphanumeric with single hyphens, repositories add '.' and '_'.
    [GeneratedRegex("^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100})$")]
    private static partial Regex GitHubRepositoryPattern();
}
