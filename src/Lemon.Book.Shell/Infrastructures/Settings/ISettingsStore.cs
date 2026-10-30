namespace Lemon.Book.Shell.Infrastructures.Settings;

/// <summary>
/// A few reader preferences (language, reading page), kept per device. Each head supplies its own store:
/// a JSON file on the desktop, <c>localStorage</c> in the browser.
/// </summary>
public interface ISettingsStore
{
    string? Get(string key);

    /// <summary>Every key with a value (for sync, which offers them all to an account on first sign-in).</summary>
    IEnumerable<string> Keys { get; }

    /// <summary>Saves a value; <see langword="null"/> removes it.</summary>
    void Set(string key, string? value);
}

/// <summary>Setting names shared by every store.</summary>
public static class SettingKeys
{
    public const string Culture = "culture";
    public const string PageIndex = "pageIndex";
    public const string AnimatesTurns = "animatesTurns";
    public const string Narration = "narration";
    public const string ShowPinyin = "showPinyin";
    public const string ReadToMe = "readToMe";
    public const string Games = "games";
}
