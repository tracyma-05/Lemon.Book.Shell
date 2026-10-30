using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Lemon.Book.Shell.Infrastructures.Localization;

/// <summary>
/// Resource-backed strings: the book's own <c>Resources/AppStrings.resx</c> (embedded as
/// <c>&lt;assembly name&gt;.Resources.AppStrings.resources</c>), then the shell's <c>Resources/ShellStrings.resx</c>, so
/// a book can word any of the shell's strings its own way. The neutral resources hold the Chinese text; the cultures
/// are the book's (<see cref="BookShellOptions.Cultures"/>), zh-CN alone unless it says otherwise.
/// </summary>
/// <remarks>
/// Ported from the Lemon desktop templates. Exposed as a singleton via <see cref="Instance"/> because XAML
/// markup needs an ambient binding source (see <see cref="LocalizeExtension"/>).
/// </remarks>
public sealed class LocalizationService : ILocalizationService
{
    private static readonly ResourceManager ShellStrings =
        new("Lemon.Book.Shell.Resources.ShellStrings", typeof(LocalizationService).Assembly);

    private static (Assembly? Book, ResourceManager? Strings) s_book;

    private CultureInfo _currentCulture = Cultures[0];

    private static CultureInfo[] Cultures => [.. BookShell.Options.Cultures.Select(CultureInfo.GetCultureInfo)];

    /// <summary>The book's own strings, or null when it has none.</summary>
    private static ResourceManager? BookStrings
    {
        get
        {
            var book = BookShell.AppAssembly;
            if (s_book.Book != book)
            {
                var name = $"{book.GetName().Name}.Resources.AppStrings";
                s_book = (book, book.GetManifestResourceNames().Contains(name + ".resources") ? new ResourceManager(name, book) : null);
            }

            return s_book.Strings;
        }
    }

    /// <summary>
    /// Property name that invalidates every indexer binding (<c>[Key]</c>) on this source. Avalonia listens for
    /// the indexer's own name, <c>"Item"</c>, not WPF's <c>"Item[]"</c>.
    /// </summary>
    internal const string IndexerPropertyName = "Item";

    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<CultureInfo> SupportedCultures => Cultures;

    public CultureInfo CurrentCulture => _currentCulture;

    public string this[string key] => GetString(key);

    public string GetString(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        // Missing keys surface as [Key] rather than an empty label, so a gap is visible instead of blank.
        return BookStrings?.GetString(key, _currentCulture) ?? ShellStrings.GetString(key, _currentCulture) ?? $"[{key}]";
    }

    public string Format(string key, params object?[] args) =>
        string.Format(_currentCulture, GetString(key), args);

    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (_currentCulture.Equals(culture))
        {
            return;
        }

        _currentCulture = culture;

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        // The indexer notification tells Avalonia every [Key] binding on this source is stale, which is what
        // makes the whole book re-read its strings without a restart.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerPropertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
    }

    /// <summary>
    /// Resolves a persisted culture name to a supported culture; with none saved, follows the system language.
    /// </summary>
    public CultureInfo ResolveSupportedCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return Match(CultureInfo.CurrentUICulture);
        }

        try
        {
            return Match(CultureInfo.GetCultureInfo(cultureName));
        }
        catch (CultureNotFoundException)
        {
            return Cultures[0];
        }
    }

    private static CultureInfo Match(CultureInfo requested) =>
        Cultures.FirstOrDefault(c => c.Equals(requested))
        ?? Cultures.FirstOrDefault(c => c.TwoLetterISOLanguageName == requested.TwoLetterISOLanguageName)
        ?? Cultures[0];
}
