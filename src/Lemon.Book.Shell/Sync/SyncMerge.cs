using System.Text.Json;
using Lemon.Book.Shell.Contents;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Sync;

/// <summary>
/// What to keep when a setting changed on this device and on another since they were last in step. Progress only ever
/// grows, so for progress both sides are kept: walkthroughs heard, bookmarks and notes here, and the book's own
/// progress by its rules (<see cref="ISyncRules.TryMerge"/>). Anything else (a preference, the page being read) is the
/// later of the two.
/// </summary>
public static class SyncMerge
{
    /// <param name="local">This device's value and when it changed here.</param>
    /// <param name="remote">The server's value and when it changed there.</param>
    public static string? Merge(string key, (string? Value, DateTime ChangedAt) local, (string? Value, DateTime ChangedAt) remote)
    {
        if (local.Value == remote.Value) return local.Value;

        // A cleared value (the settings' 清除进度, a removed note list) against a change: the later wins.
        if (local.Value is null || remote.Value is null) return Later(local, remote);

        try
        {
            return key switch
            {
                ReadingProgress.SettingKey => Progress(local.Value, remote.Value),
                Bookmarks.SettingKey => BookmarkList(local.Value, remote.Value),
                Notes.SettingKey => NoteList(local.Value, remote.Value),
                _ when BookShell.SyncRules.TryMerge(key, local.Value, remote.Value, out var merged) => merged,
                _ => Later(local, remote),
            };
        }
        catch (JsonException)
        {
            // One side unreadable (an older or newer format): keep the later rather than lose both.
            return Later(local, remote);
        }
    }

    private static string? Later((string? Value, DateTime ChangedAt) local, (string? Value, DateTime ChangedAt) remote) =>
        local.ChangedAt >= remote.ChangedAt ? local.Value : remote.Value;

    /// <summary>Both sides of a set kept as one string (books finished, games done), in order.</summary>
    public static string UnionSet(string local, string remote, char separator) =>
        string.Join(separator, SyncEvents.SplitSet(local, separator).Union(SyncEvents.SplitSet(remote, separator)).Order(StringComparer.Ordinal));

    private static string Progress(string local, string remote)
    {
        var merged = JsonSerializer.Deserialize(remote, BookJson.Default.DictionaryStringChapterProgressRecord) ?? [];
        foreach (var (slug, record) in JsonSerializer.Deserialize(local, BookJson.Default.DictionaryStringChapterProgressRecord) ?? [])
        {
            var other = merged.GetValueOrDefault(slug);
            merged[slug] = other is null
                ? record
                : new ChapterProgressRecord
                {
                    Opened = record.Opened || other.Opened,
                    Walkthroughs = [.. other.Walkthroughs.Union(record.Walkthroughs)],
                };
        }

        return JsonSerializer.Serialize(merged, BookJson.Default.DictionaryStringChapterProgressRecord);
    }

    private static string BookmarkList(string local, string remote)
    {
        var merged = (JsonSerializer.Deserialize(local, BookJson.Default.ListBookmarkRecord) ?? [])
            .Concat(JsonSerializer.Deserialize(remote, BookJson.Default.ListBookmarkRecord) ?? [])
            .GroupBy(b => b.Page)
            .Select(g => g.MinBy(b => b.Added)!)
            .OrderByDescending(b => b.Added)
            .ToList();
        return JsonSerializer.Serialize(merged, BookJson.Default.ListBookmarkRecord);
    }

    private static string NoteList(string local, string remote)
    {
        var merged = (JsonSerializer.Deserialize(local, BookJson.Default.ListNoteRecord) ?? [])
            .Concat(JsonSerializer.Deserialize(remote, BookJson.Default.ListNoteRecord) ?? [])
            .GroupBy(n => n.Id)
            .Select(g => g.MaxBy(n => n.Updated)!)
            .OrderByDescending(n => n.Created)
            .ToList();
        return JsonSerializer.Serialize(merged, BookJson.Default.ListNoteRecord);
    }
}
