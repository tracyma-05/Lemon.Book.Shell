using System.Globalization;
using System.Resources;
using Lemon.Book.Shell.Infrastructures.Localization;
using Xunit;

namespace Lemon.Book.Shell.Tests;

/// <summary>
/// The shell's own strings: Chinese (the neutral resources) and English (for a book written in both), every key in
/// both, and none naming a book, since every book shows them.
/// </summary>
public class ShellStringsTests
{
    private static readonly ResourceManager Strings =
        new("Lemon.Book.Shell.Resources.ShellStrings", typeof(LocalizationService).Assembly);

    private static Dictionary<string, string> Read(CultureInfo culture) =>
        Strings.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    [Fact]
    public void Every_string_is_in_Chinese_and_in_English()
    {
        var chinese = Read(CultureInfo.InvariantCulture);
        var english = Read(CultureInfo.GetCultureInfo("en"));

        Assert.Equal(chinese.Keys.Order(), english.Keys.Order());
        Assert.All(english, e => Assert.False(string.IsNullOrWhiteSpace(e.Value), e.Key));
        // The same placeholders, so a book's Format works in either language.
        Assert.All(chinese, c => Assert.Equal(Placeholders(c.Value), Placeholders(english[c.Key])));
    }

    [Fact]
    public void No_string_names_a_book()
    {
        string[] names = ["柠檬", "绘本", "数学之书", "Maths Book", "Explore Book", "Picture Book"];
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en") })
        {
            Assert.All(Read(culture), s => Assert.DoesNotContain(names, n => s.Value.Contains(n, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void A_book_in_English_and_Chinese_gets_the_shells_strings_in_its_language()
    {
        var before = BookShell.Options;
        var localization = LocalizationService.Instance;
        var culture = localization.CurrentCulture;
        try
        {
            BookShell.Configure(before with { Cultures = ["en", "zh-CN"] });

            localization.SetCulture(localization.ResolveSupportedCulture("en-GB"));
            Assert.Equal("Bookmarks", localization.GetString("Bookmarks_Title"));

            localization.SetCulture(localization.ResolveSupportedCulture("zh-CN"));
            Assert.Equal("书签", localization.GetString("Bookmarks_Title"));
        }
        finally
        {
            BookShell.Configure(before);
            localization.SetCulture(culture);
        }
    }

    private static string[] Placeholders(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order()];
}
