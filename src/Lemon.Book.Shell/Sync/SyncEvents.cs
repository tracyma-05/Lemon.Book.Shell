using System.Globalization;
using System.Text.Json;
using Lemon.Book.Shell.Contents;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Sync;

/// <summary>
/// The child's history for the account: a book opened, a walkthrough heard, and whatever the book records of its own
/// (<see cref="ISyncRules.Derive"/>: a page seen, a book finished, a word learned). Read off the changes to the
/// settings (old value against new), so the pages and games know nothing about it. Kept on the device until sent, signed in or not, so a child who signs in
/// later still brings their history; capped so a device that never signs in does not grow without end.
/// </summary>
public sealed class SyncEvents
{
    internal const string OutboxKey = "sync.events";
    internal const int MaxKept = 5000;

    private readonly ISettingsStore _store;
    private readonly Func<DateTime> _now;
    private List<SyncEventRecord> _outbox;

    internal SyncEvents(ISettingsStore store, Func<DateTime> now)
    {
        _store = store;
        _now = now;
        _outbox = Load(store);
    }

    public IReadOnlyList<SyncEventRecord> Pending => _outbox;

    /// <summary>Something the child just did on this device (not from another device): the pet cheers at once.</summary>
    public event EventHandler<SyncEventRecord>? Recorded;

    /// <summary>Sent: forgets them.</summary>
    internal void Sent(IEnumerable<Guid> ids)
    {
        var sent = ids.ToHashSet();
        if (_outbox.RemoveAll(e => sent.Contains(e.Id)) > 0) Save();
    }

    internal void Derive(string key, string? old, string? value)
    {
        var events = key == ReadingProgress.SettingKey
            ? Walkthroughs(old, value)
            : BookShell.SyncRules.Derive(key, old, value).Select(e => Event(e.Type, e.Subject, e.Data));

        var recorded = new List<SyncEventRecord>();
        foreach (var e in events)
        {
            _outbox.Add(e);
            recorded.Add(e);
        }

        if (recorded.Count == 0) return;
        if (_outbox.Count > MaxKept) _outbox = _outbox[^MaxKept..];
        Save();
        foreach (var e in recorded) Recorded?.Invoke(this, e);
    }

    private SyncEventRecord Event(string type, string? subject, string? data = null) =>
        new() { Id = Guid.NewGuid(), Type = type, Subject = subject, Data = data, OccurredAt = _now() };

    private IEnumerable<SyncEventRecord> Walkthroughs(string? old, string? value)
    {
        var before = Read(old, BookJson.Default.DictionaryStringChapterProgressRecord) ?? [];
        foreach (var (slug, record) in Read(value, BookJson.Default.DictionaryStringChapterProgressRecord) ?? [])
        {
            var was = before.GetValueOrDefault(slug);
            if (record.Opened && was?.Opened != true) yield return Event("book-opened", slug);
            foreach (var view in record.Walkthroughs.Except(was?.Walkthroughs ?? []))
            {
                yield return Event("walkthrough-done", slug, Json(("view", view)));
            }
        }
    }

    /// <summary>A set kept as one string (books finished, games done): its items.</summary>
    public static IReadOnlySet<string> SplitSet(string? value, char separator) =>
        (value ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>An event's data: a small JSON object of strings.</summary>
    public static string Json(params (string Name, string Value)[] fields) =>
        JsonSerializer.Serialize(fields.ToDictionary(f => f.Name, f => f.Value), BookJson.Default.DictionaryStringString);

    private static T? Read<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        if (string.IsNullOrEmpty(json)) return default;
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private void Save() =>
        _store.Set(OutboxKey, _outbox.Count == 0 ? null : JsonSerializer.Serialize(_outbox, SyncJson.Default.ListSyncEventRecord));

    private static List<SyncEventRecord> Load(ISettingsStore store)
    {
        try
        {
            return store.Get(OutboxKey) is { } json ? JsonSerializer.Deserialize(json, SyncJson.Default.ListSyncEventRecord) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
