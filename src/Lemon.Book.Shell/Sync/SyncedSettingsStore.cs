using System.Text.Json;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Sync;

/// <summary>
/// The book's settings store, keeping track of what to sync. Everything still saves on the device first (the store it
/// wraps), so the book works the same signed in or not, online or not. Every setting syncs except the few that belong
/// to the device (<see cref="IsDeviceOnly"/>), so a new kind of progress syncs without anything to add here.
/// For each synced key a journal keeps the server version it was last in step with and whether it changed here since;
/// changes also become events for the account's history (<see cref="SyncEvents"/>).
/// </summary>
public sealed class SyncedSettingsStore : ISettingsStore
{
    internal const string JournalKey = "sync.journal";
    internal const string CursorKey = "sync.cursor";
    internal const string DeviceIdKey = "sync.deviceId";

    private readonly ISettingsStore _inner;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, SyncJournalEntry> _journal;

    public SyncedSettingsStore(ISettingsStore inner, Func<DateTime>? now = null)
    {
        _inner = inner;
        _now = now ?? (() => DateTime.UtcNow);
        _journal = LoadJournal(inner);
        Events = new SyncEvents(inner, _now);
    }

    /// <summary>The device's store underneath, for what never syncs (the account, the journal).</summary>
    public ISettingsStore Inner => _inner;

    /// <summary>The events waiting to be sent.</summary>
    public SyncEvents Events { get; }

    /// <summary>A synced setting changed on this device.</summary>
    public event EventHandler? LocalChanged;

    /// <summary>
    /// Settings that stay on this device: the window, the update address, the account and the sync's own records.
    /// Everything else (progress, the 生词本, notes, bookmarks, the page being read, preferences) syncs.
    /// </summary>
    public static bool IsDeviceOnly(string key) =>
        key is "window" or "pet.position" or "update.feed" or "update.checkOnStartup"
        || key.StartsWith("account.", StringComparison.Ordinal)
        || key.StartsWith("sync.", StringComparison.Ordinal);

    public string? Get(string key) => _inner.Get(key);

    public IEnumerable<string> Keys => _inner.Keys;

    public void Set(string key, string? value)
    {
        var old = _inner.Get(key);
        if (old == value) return;
        _inner.Set(key, value);
        if (IsDeviceOnly(key)) return;

        var entry = _journal.GetValueOrDefault(key) ?? new SyncJournalEntry();
        _journal[key] = entry with { Dirty = true, ChangedAt = _now() };
        SaveJournal();
        Events.Derive(key, old, value);
        LocalChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where this device has caught up to on the server.</summary>
    public long Cursor
    {
        get => long.TryParse(_inner.Get(CursorKey), out var cursor) ? cursor : 0;
        set => _inner.Set(CursorKey, value == 0 ? null : value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>This device's random id on the server, made the first time it is asked for.</summary>
    public string DeviceId
    {
        get
        {
            if (_inner.Get(DeviceIdKey) is { Length: > 0 } id) return id;
            id = Guid.NewGuid().ToString("N");
            _inner.Set(DeviceIdKey, id);
            return id;
        }
    }

    public SyncJournalEntry? JournalOf(string key) => _journal.GetValueOrDefault(key);

    public bool HasPendingChanges => _journal.Values.Any(e => e.Dirty);

    /// <summary>The settings changed here since they were last in step with the server, as the server wants them.</summary>
    internal List<SyncChange> PendingChanges() =>
        _journal.Where(e => e.Value.Dirty)
            .Select(e => new SyncChange { Key = e.Key, Value = _inner.Get(e.Key), BaseVersion = e.Value.Version, ChangedAt = e.Value.ChangedAt })
            .ToList();

    /// <summary>
    /// The server took <paramref name="sent"/> as <paramref name="version"/>. If the setting changed again while it was
    /// on its way, it stays to send (over the new version).
    /// </summary>
    internal void Accepted(string key, string? sent, long version)
    {
        var entry = _journal.GetValueOrDefault(key) ?? new SyncJournalEntry();
        _journal[key] = entry with { Version = version, Dirty = _inner.Get(key) != sent };
        SaveJournal();
    }

    /// <summary>
    /// Takes a value from the server (another device's, or a merge of it with this device's): saved without becoming
    /// a local change. <paramref name="dirty"/> when the value still differs from the server's and must be sent back.
    /// Returns whether the value here changed.
    /// </summary>
    internal bool ApplyFromServer(string key, string? value, long version, bool dirty = false)
    {
        var changed = _inner.Get(key) != value;
        if (changed) _inner.Set(key, value);
        var entry = _journal.GetValueOrDefault(key) ?? new SyncJournalEntry();
        _journal[key] = entry with { Version = version, Dirty = dirty, ChangedAt = dirty ? _now() : entry.ChangedAt };
        SaveJournal();
        return changed;
    }

    /// <summary>
    /// Starting over with an account (signing in, or out): no value has a server version any more, and every setting
    /// this device has is to be sent, to be merged with what the account already has.
    /// </summary>
    internal void ResetForAccount()
    {
        _journal.Clear();
        var now = _now();
        foreach (var key in _inner.Keys.Where(k => !IsDeviceOnly(k) && _inner.Get(k) is not null))
        {
            _journal[key] = new SyncJournalEntry { Dirty = true, ChangedAt = now };
        }

        Cursor = 0;
        SaveJournal();
    }

    private void SaveJournal() =>
        _inner.Set(JournalKey, _journal.Count == 0 ? null : JsonSerializer.Serialize(_journal, SyncJson.Default.DictionaryStringSyncJournalEntry));

    private static Dictionary<string, SyncJournalEntry> LoadJournal(ISettingsStore store)
    {
        try
        {
            var saved = store.Get(JournalKey) is { } json
                ? JsonSerializer.Deserialize(json, SyncJson.Default.DictionaryStringSyncJournalEntry)
                : null;
            return new Dictionary<string, SyncJournalEntry>(saved ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, SyncJournalEntry>(StringComparer.Ordinal);
        }
    }
}
