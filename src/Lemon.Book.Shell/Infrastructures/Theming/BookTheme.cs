using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Infrastructures.Theming;

/// <summary>Light paper, dark paper, whichever the system uses, or the soft green eye-care paper (护眼).</summary>
public enum BookThemeMode
{
    Light,
    Dark,
    System,
    EyeCare,
}

/// <summary>An accent colour, with the shade used on light paper and the one used on dark paper.</summary>
public sealed record AccentColor(string Id, Color OnLight, Color OnDark)
{
    /// <summary>Resource key of the colour's name, e.g. <c>Theme_Accent_Terracotta</c>.</summary>
    public string NameKey => "Theme_Accent_" + Id;
}

/// <summary>
/// Applies the reader's theme and accent colour to the application: the requested theme variant picks the paper and
/// ink of <c>App.axaml</c>'s theme dictionaries, and the accent replaces the accent brushes (and Fluent's accent, so
/// sliders and check boxes match).
/// </summary>
public static class BookTheme
{
    public const string ThemeSetting = "theme";
    public const string AccentSetting = "accent";

    /// <summary>
    /// The eye-care paper: a muted green with softer ink, for long reading. It builds on the light theme, so Fluent's
    /// controls (and any brush it does not set) look as they do on light paper.
    /// </summary>
    public static ThemeVariant EyeCare { get; } = new("EyeCare", ThemeVariant.Light);

    public static IReadOnlyList<AccentColor> Accents { get; } =
    [
        new("Terracotta", Color.Parse("#B5532E"), Color.Parse("#E38A62")),
        new("Indigo", Color.Parse("#3F5AA6"), Color.Parse("#8EA6F0")),
        new("Pine", Color.Parse("#2E7D5B"), Color.Parse("#6CC69A")),
        new("Plum", Color.Parse("#8A4A8F"), Color.Parse("#CF94D4")),
        new("Amber", Color.Parse("#A8741A"), Color.Parse("#E5B655")),
    ];

    public static AccentColor DefaultAccent => Accents[0];

    public static BookThemeMode LoadMode(ISettingsStore store) =>
        Enum.TryParse<BookThemeMode>(store.Get(ThemeSetting), out var mode) ? mode : BookThemeMode.Light;

    public static AccentColor LoadAccent(ISettingsStore store) =>
        Accents.FirstOrDefault(a => a.Id == store.Get(AccentSetting)) ?? DefaultAccent;

    /// <summary>Applies the saved theme and accent (at start-up).</summary>
    public static void Apply(Application? app, ISettingsStore store) => Apply(app, LoadMode(store), LoadAccent(store));

    public static void Apply(Application? app, BookThemeMode mode, AccentColor accent)
    {
        if (app is null)
        {
            return;
        }

        app.RequestedThemeVariant = mode switch
        {
            BookThemeMode.Dark => ThemeVariant.Dark,
            BookThemeMode.System => ThemeVariant.Default,
            BookThemeMode.EyeCare => EyeCare,
            _ => ThemeVariant.Light,
        };

        SetAccent(app.Resources, ThemeVariant.Light, accent.OnLight);
        SetAccent(app.Resources, ThemeVariant.Dark, accent.OnDark);
        // A shade darker on the green paper, so accent text keeps its contrast.
        SetAccent(app.Resources, EyeCare, Mix(accent.OnLight, Colors.Black, 0.12));
    }

    private static void SetAccent(IResourceDictionary resources, ThemeVariant variant, Color color)
    {
        if (!resources.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not IResourceDictionary theme)
        {
            theme = new ResourceDictionary();
            resources.ThemeDictionaries[variant] = (IThemeVariantProvider)theme;
        }

        theme["AccentBrush"] = new SolidColorBrush(color);
        theme["AccentSoftBrush"] = new SolidColorBrush(WithAlpha(color, (byte)(variant == ThemeVariant.Dark ? 0x33 : 0x22)));
        theme["AccentFaintBrush"] = new SolidColorBrush(WithAlpha(color, (byte)(variant == ThemeVariant.Dark ? 0x1F : 0x14)));
        theme["AccentSelectionBrush"] = new SolidColorBrush(WithAlpha(color, (byte)(variant == ThemeVariant.Dark ? 0x66 : 0x55)));

        // Fluent's own accent (slider tracks, check boxes, focus), with its lighter and darker steps.
        theme["SystemAccentColor"] = color;
        for (var step = 1; step <= 3; step++)
        {
            theme[$"SystemAccentColorLight{step}"] = Mix(color, Colors.White, 0.18 * step);
            theme[$"SystemAccentColorDark{step}"] = Mix(color, Colors.Black, 0.18 * step);
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));
}
