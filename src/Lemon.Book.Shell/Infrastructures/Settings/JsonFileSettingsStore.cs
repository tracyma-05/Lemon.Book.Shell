using System.Text.Json;

namespace Lemon.Book.Shell.Infrastructures.Settings;

/// <summary>
/// Settings in a small JSON file, written on every change. Used by the desktop head; the file lives in the
/// user's application-data folder, never next to the program.
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values;

    public JsonFileSettingsStore(string path)
    {
        _path = path;
        _values = Load(path);
    }

    /// <summary>The first book's location: <c>%APPDATA%\Lemon.Book.Shell</c> or <c>~/Library/Application Support/Lemon.Book.Shell</c>.</summary>
    public static string DefaultPath() => DefaultPath("Lemon.Book.Shell");

    /// <summary>
    /// The book's own location, <c>%APPDATA%\&lt;folder&gt;</c> or <c>~/Library/Application Support/&lt;folder&gt;</c>:
    /// a book that kept its settings before it took the shell passes the folder it used, so they are still there.
    /// </summary>
    public static string DefaultPath(string folder) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            folder,
            "settings.json");

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

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_values, BookJson.Default.DictionaryStringString));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that cannot be saved is not worth interrupting the reader for; it still applies now.
        }
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), BookJson.Default.DictionaryStringString)
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged or unreadable file means starting from defaults, not failing to open the book.
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
