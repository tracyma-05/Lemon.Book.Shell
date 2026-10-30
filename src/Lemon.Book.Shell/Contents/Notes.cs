using System.Text.Json;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Contents;

/// <summary>A reader's note on a page: words they quoted from it (optional) and their own text.</summary>
public sealed record NoteRecord(string Id, int Page, string? Quote, string Text, DateTime Created, DateTime Updated);

/// <summary>The reader's notes, newest first, saved on this device as one setting.</summary>
public sealed class Notes
{
    public const string SettingKey = "notes";

    private readonly ISettingsStore _store;
    private List<NoteRecord> _items;

    public Notes(ISettingsStore store)
    {
        _store = store;
        _items = Load(store);
    }

    public IReadOnlyList<NoteRecord> Items => _items;

    public event EventHandler? Changed;

    /// <summary>Adds a note; one with neither a quote nor any text is not kept.</summary>
    public NoteRecord? Add(int page, string? quote, string text)
    {
        quote = string.IsNullOrWhiteSpace(quote) ? null : quote.Trim();
        text = text.Trim();
        if (quote is null && text.Length == 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var note = new NoteRecord(Guid.NewGuid().ToString("N"), page, quote, text, now, now);
        _items.Insert(0, note);
        Save();
        return note;
    }

    public void Update(string id, string text)
    {
        var index = _items.FindIndex(n => n.Id == id);
        if (index < 0 || _items[index].Text == text.Trim())
        {
            return;
        }

        _items[index] = _items[index] with { Text = text.Trim(), Updated = DateTime.UtcNow };
        Save();
    }

    public void Remove(string id)
    {
        if (_items.RemoveAll(n => n.Id == id) > 0) Save();
    }

    private void Save()
    {
        _store.Set(SettingKey, JsonSerializer.Serialize(_items, BookJson.Default.ListNoteRecord));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<NoteRecord> Load(ISettingsStore store)
    {
        try
        {
            var saved = store.Get(SettingKey) is { } json ? JsonSerializer.Deserialize(json, BookJson.Default.ListNoteRecord) : null;

            // A note on a page that no longer exists is kept, filed under the last page, rather than lost.
            return saved?
                .Where(n => !string.IsNullOrEmpty(n.Id))
                .Select(n => n with { Page = Math.Clamp(n.Page, 0, BookShell.PageCount - 1), Text = n.Text ?? string.Empty })
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
