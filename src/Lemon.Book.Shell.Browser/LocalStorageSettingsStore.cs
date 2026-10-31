using System.Runtime.InteropServices.JavaScript;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Browser;

/// <summary>Reader preferences in the browser's <c>localStorage</c>, under a per-book prefix.</summary>
/// <param name="prefix">
/// The book's prefix (<c>picture-book:</c>, <c>maths-book:</c>): a book keeps the one it saved under before, and two
/// books served from one site keep apart.
/// </param>
public sealed partial class LocalStorageSettingsStore(string prefix) : ISettingsStore
{
    /// <summary>The first book's prefix.</summary>
    public const string DefaultPrefix = "picture-book:";

    private readonly string _prefix = prefix;

    public LocalStorageSettingsStore()
        : this(DefaultPrefix)
    {
    }

    public string? Get(string key)
    {
        try
        {
            return GetItem(_prefix + key);
        }
        catch (JSException)
        {
            // Storage can be blocked (private mode, site data disabled); the book then simply forgets.
            return null;
        }
    }

    public IEnumerable<string> Keys
    {
        get
        {
            var keys = new List<string>();
            try
            {
                // key(i) is null past the end.
                for (var i = 0; KeyAt(i) is { } name; i++)
                {
                    if (name.StartsWith(_prefix, StringComparison.Ordinal)) keys.Add(name[_prefix.Length..]);
                }
            }
            catch (JSException)
            {
                // Blocked storage has no keys.
            }

            return keys;
        }
    }

    public void Set(string key, string? value)
    {
        try
        {
            if (value is null)
            {
                RemoveItem(_prefix + key);
            }
            else
            {
                SetItem(_prefix + key, value);
            }
        }
        catch (JSException)
        {
            // See Get: a preference that cannot be stored still applies for this visit.
        }
    }

    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);

    [JSImport("globalThis.localStorage.key")]
    private static partial string? KeyAt(int index);

    [JSImport("globalThis.localStorage.setItem")]
    private static partial void SetItem(string key, string value);

    [JSImport("globalThis.localStorage.removeItem")]
    private static partial void RemoveItem(string key);
}
