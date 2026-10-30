using System.Text.Json;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Contents;

/// <summary>A saved bookmark: the page and when it was added.</summary>
public sealed record BookmarkRecord(int Page, DateTime Added);

/// <summary>The reader's bookmarks, newest first, saved on this device as one setting.</summary>
public sealed class Bookmarks
{
    public const string SettingKey = "bookmarks";

    private readonly ISettingsStore _store;
    private List<BookmarkRecord> _items;

    public Bookmarks(ISettingsStore store)
    {
        _store = store;
        _items = Load(store);
    }

    public IReadOnlyList<BookmarkRecord> Items => _items;

    public event EventHandler? Changed;

    public bool Contains(int page) => _items.Any(b => b.Page == page);

    /// <summary>Adds a bookmark at <paramref name="page"/>, or removes the one that is there.</summary>
    public void Toggle(int page)
    {
        if (Contains(page)) Remove(page);
        else Add(page);
    }

    public void Add(int page)
    {
        if (Contains(page)) return;
        _items.Insert(0, new BookmarkRecord(page, DateTime.UtcNow));
        Save();
    }

    public void Remove(int page)
    {
        if (_items.RemoveAll(b => b.Page == page) > 0) Save();
    }

    private void Save()
    {
        _store.Set(SettingKey, JsonSerializer.Serialize(_items, BookJson.Default.ListBookmarkRecord));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<BookmarkRecord> Load(ISettingsStore store)
    {
        try
        {
            var saved = store.Get(SettingKey) is { } json ? JsonSerializer.Deserialize(json, BookJson.Default.ListBookmarkRecord) : null;

            // Pages that no longer exist (a shorter book) are dropped.
            return saved?.Where(b => b.Page >= 0 && b.Page < BookShell.PageCount).DistinctBy(b => b.Page).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
