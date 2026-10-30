namespace Lemon.Book.Shell.Sync;

/// <summary>
/// A book's own kinds of progress, for sync: the shell merges and records its own (reading progress, bookmarks,
/// notes) and asks the book about every other key. A key neither knows is a preference: the later change wins and
/// nothing is recorded.
/// </summary>
public interface ISyncRules
{
    /// <summary>
    /// Both sides of a key the book keeps, changed on this device and on another: what to keep (both, as a rule, since
    /// progress only grows). False for a key that is not the book's.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">One side is unreadable: the later is kept.</exception>
    bool TryMerge(string key, string local, string remote, out string merged);

    /// <summary>What the child did, read off a change to a key (old value against new): none for a key not the book's.</summary>
    IEnumerable<SyncEventDraft> Derive(string key, string? old, string? value);
}

/// <summary>An event for the account's history before it has an id and a time.</summary>
public sealed record SyncEventDraft(string Type, string? Subject, string? Data = null);

/// <summary>Rules for a book with no progress of its own.</summary>
public sealed class SyncRules : ISyncRules
{
    public static ISyncRules None { get; } = new SyncRules();

    private SyncRules()
    {
    }

    public bool TryMerge(string key, string local, string remote, out string merged)
    {
        merged = local;
        return false;
    }

    public IEnumerable<SyncEventDraft> Derive(string key, string? old, string? value) => [];
}
