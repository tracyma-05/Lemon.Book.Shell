using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Lemon.Book.Shell;
using Lemon.Book.Shell.Infrastructures.Settings;
using Lemon.Book.Shell.Sync;
using Lemon.Book.Shell.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Sequential on purpose: the headless platform does not like parallel start-up, and several tests change
// process-wide state (the current culture, LocalizationService.Instance, BookShell's options).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Lemon.Book.Shell.Tests;

/// <summary>
/// Headless host for <c>[AvaloniaFact]</c> tests: an application with the shell's look and nothing else, so the
/// shell is tested as a book would use it.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>The tests' book: this assembly (its narration manifest and strings), with a few rules of its own.</summary>
    [ModuleInitializer]
    internal static void ConfigureShell() =>
        BookShell.Configure(new BookShellOptions(typeof(TestAppBuilder).Assembly, "test-book", () => 100, TestSyncRules.Instance));
}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Lemon.Book.Shell.Tests/")) { Source = new Uri("avares://Lemon.Book.Shell/Styles/Book.axaml") });
    }
}

/// <summary>A book's rules, as a book would write them: books finished and games done are sets that only grow.</summary>
public sealed class TestSyncRules : ISyncRules
{
    public const string Finished = "finishedBooks";

    public static TestSyncRules Instance { get; } = new();

    public bool TryMerge(string key, string local, string remote, out string merged)
    {
        merged = key switch
        {
            Finished => SyncMerge.UnionSet(local, remote, ','),
            SettingKeys.Games => SyncMerge.UnionSet(local, remote, '\n'),
            _ => local,
        };
        return key is Finished or SettingKeys.Games;
    }

    public IEnumerable<SyncEventDraft> Derive(string key, string? old, string? value) => key switch
    {
        Finished => Added(old, value, ',').Select(slug => new SyncEventDraft("book-finished", slug)),
        SettingKeys.Games => Added(old, value, '\n').Select(id => new SyncEventDraft("game-done", id)),
        _ => [],
    };

    private static IEnumerable<string> Added(string? old, string? value, char separator)
    {
        var before = SyncEvents.SplitSet(old, separator);
        return SyncEvents.SplitSet(value, separator).Where(x => !before.Contains(x));
    }
}
