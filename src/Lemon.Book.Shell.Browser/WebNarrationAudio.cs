using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Lemon.Book.Shell.Explore.Narration;

namespace Lemon.Book.Shell.Browser;

/// <summary>
/// Narration clips served next to the app under <c>narration/</c>, played in an audio element routed through Web Audio (<c>wwwroot/narration.js</c>).
/// A clip that cannot be fetched (not deployed, or offline and never cached) raises <see cref="Failed"/>, and the
/// narration carries on as subtitles.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class WebNarrationAudio : INarrationAudio
{
    public const string ModuleName = "narration";

    /// <summary>Relative to <c>_framework/dotnet.js</c>, which imports it: the package's static web asset.</summary>
    public const string ModuleUrl = "../_content/Lemon.Book.Shell.Browser/narration.js";

    public event EventHandler? Failed;

    // Whether the server has the file is only known once it is fetched; see Failed.
    public bool Has(string clip) => true;

    public bool Play(string clip, double volume)
    {
        PlayClip("narration/" + clip, volume, () => Failed?.Invoke(this, EventArgs.Empty));
        return true;
    }

    public void Pause() => PauseClip();

    public void Resume() => ResumeClip();

    public void Stop() => StopClip();

    public double Volume
    {
        set => SetVolume(Math.Clamp(value, 0, 1));
    }

    public double Rate
    {
        set => SetRate(Math.Clamp(value, 0.5, 2));
    }

    public double Position => ClipPosition();

    public bool IsEnded => ClipEnded();

    [JSImport("play", ModuleName)]
    private static partial void PlayClip(string url, double volume, [JSMarshalAs<JSType.Function>] Action onFailed);

    [JSImport("pause", ModuleName)]
    private static partial void PauseClip();

    [JSImport("resume", ModuleName)]
    private static partial void ResumeClip();

    [JSImport("stop", ModuleName)]
    private static partial void StopClip();

    [JSImport("setVolume", ModuleName)]
    private static partial void SetVolume(double volume);

    [JSImport("setRate", ModuleName)]
    private static partial void SetRate(double rate);

    [JSImport("position", ModuleName)]
    private static partial double ClipPosition();

    [JSImport("isEnded", ModuleName)]
    private static partial bool ClipEnded();
}
