using System.Text.Json;
using Lemon.Book.Shell.Explore.Lessons;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Contents;

/// <summary>What the reader has done in one chapter: opened it, and which of its walkthroughs they heard to the end.</summary>
public sealed record ChapterProgressRecord
{
    public bool Opened { get; init; }

    /// <summary>The views (modes) whose walkthrough ran to the end.</summary>
    public List<string> Walkthroughs { get; init; } = [];
}

/// <summary>A chapter's progress for the table of contents.</summary>
public readonly record struct ChapterProgress(bool Opened, int WalkthroughsDone, int WalkthroughCount)
{
    public bool IsComplete => WalkthroughCount > 0 && WalkthroughsDone >= WalkthroughCount;
}

/// <summary>
/// Reading progress, kept on this device: which chapters were opened and which walkthroughs were finished. Shown in
/// the table of contents; the settings can clear it.
/// </summary>
public sealed class ReadingProgress
{
    public const string SettingKey = "progress";

    private readonly ISettingsStore _store;
    private readonly Dictionary<string, IReadOnlyList<string>> _views = new(StringComparer.Ordinal);
    private Dictionary<string, ChapterProgressRecord> _chapters;

    public ReadingProgress(ISettingsStore store)
    {
        _store = store;
        _chapters = Load(store);
    }

    public event EventHandler? Changed;

    public ChapterProgress For(string slug)
    {
        _chapters.TryGetValue(slug, out var record);
        var views = _views.GetValueOrDefault(slug) ?? [];
        var done = record?.Walkthroughs.Count(views.Contains) ?? 0;
        return new ChapterProgress(record?.Opened ?? false, done, views.Count);
    }

    /// <summary>Chapters opened and chapters with every walkthrough finished.</summary>
    public (int Opened, int Complete) Summary() =>
        (_chapters.Count(c => c.Value.Opened), _chapters.Keys.Count(slug => For(slug).IsComplete));

    /// <summary>
    /// Follows a chapter's walkthrough: when it runs to the end, the view it was for (<paramref name="view"/>, read at
    /// that moment) counts as done. <paramref name="views"/> are all of the chapter's views with a walkthrough.
    /// </summary>
    public void Track(string slug, LessonPlayer lesson, Func<string> view, IReadOnlyList<string> views)
    {
        _views[slug] = views;
        lesson.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LessonPlayer.State) && lesson.State == LessonState.Done)
            {
                MarkWalkthrough(slug, view());
            }
        };
    }

    public void MarkOpened(string slug)
    {
        var record = _chapters.GetValueOrDefault(slug) ?? new ChapterProgressRecord();
        if (record.Opened) return;
        _chapters[slug] = record with { Opened = true };
        Save();
    }

    public void MarkWalkthrough(string slug, string view)
    {
        var record = _chapters.GetValueOrDefault(slug) ?? new ChapterProgressRecord();
        if (record.Walkthroughs.Contains(view) && record.Opened) return;
        _chapters[slug] = record with { Opened = true, Walkthroughs = [.. record.Walkthroughs.Union([view])] };
        Save();
    }

    public void Clear()
    {
        if (_chapters.Count == 0) return;
        _chapters = new Dictionary<string, ChapterProgressRecord>(StringComparer.Ordinal);
        Save();
    }

    private void Save()
    {
        _store.Set(SettingKey, JsonSerializer.Serialize(_chapters, BookJson.Default.DictionaryStringChapterProgressRecord));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Dictionary<string, ChapterProgressRecord> Load(ISettingsStore store)
    {
        try
        {
            var saved = store.Get(SettingKey) is { } json
                ? JsonSerializer.Deserialize(json, BookJson.Default.DictionaryStringChapterProgressRecord)
                : null;
            return new Dictionary<string, ChapterProgressRecord>(saved ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, ChapterProgressRecord>(StringComparer.Ordinal);
        }
    }
}
