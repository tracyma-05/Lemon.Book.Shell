using System.Runtime.InteropServices.JavaScript;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Browser;

/// <summary>Reader preferences in the browser's <c>localStorage</c>, under a per-app prefix.</summary>
public sealed partial class LocalStorageSettingsStore : ISettingsStore
{
    private const string Prefix = "picture-book:";

    public string? Get(string key)
    {
        try
        {
            return GetItem(Prefix + key);
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
                    if (name.StartsWith(Prefix, StringComparison.Ordinal)) keys.Add(name[Prefix.Length..]);
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
                RemoveItem(Prefix + key);
            }
            else
            {
                SetItem(Prefix + key, value);
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
