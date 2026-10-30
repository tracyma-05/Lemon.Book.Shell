using System.ComponentModel;
using System.Globalization;

namespace Lemon.Book.Shell.Infrastructures.Localization;

/// <summary>
/// Resolves UI strings for the active culture and notifies bindings when that culture changes.
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>Cultures the book ships strings for. The first entry is the neutral/fallback culture.</summary>
    IReadOnlyList<CultureInfo> SupportedCultures { get; }

    /// <summary>The culture currently used to resolve strings.</summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>Indexer so XAML can bind to a key: <c>{Binding [Some_Key], Source=...}</c>.</summary>
    string this[string key] { get; }

    string GetString(string key);

    /// <summary>Formats a resource whose value contains <c>{0}</c>-style placeholders.</summary>
    string Format(string key, params object?[] args);

    void SetCulture(CultureInfo culture);
}
