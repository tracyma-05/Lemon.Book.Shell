namespace Lemon.Book.Shell.Infrastructures.Settings;

/// <summary>Keeps settings for the current run only: the default until a head provides a lasting store, and for tests.</summary>
public sealed class MemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public IEnumerable<string> Keys => _values.Keys.ToList();

    public void Set(string key, string? value)
    {
        if (value is null)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }
    }
}
