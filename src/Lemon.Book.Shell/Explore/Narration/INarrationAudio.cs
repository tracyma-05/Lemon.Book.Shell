namespace Lemon.Book.Shell.Explore.Narration;

/// <summary>
/// Plays narration clips on one platform: a desktop audio library, or Web Audio in the browser. It knows where a
/// clip's audio is (a downloaded pack, a same-origin URL); the narration service decides what to play and when.
/// </summary>
public interface INarrationAudio
{
    /// <summary>True when the clip's audio is available here (downloaded, or reachable).</summary>
    /// <param name="clip">The clip's path in the narration folder (<see cref="NarrationManifest.ClipPath"/>).</param>
    bool Has(string clip);

    /// <summary>Starts the clip from the beginning, replacing whatever was playing; false if it cannot be played.</summary>
    bool Play(string clip, double volume);

    void Pause();

    void Resume();

    void Stop();

    double Volume { set; }

    /// <summary>
    /// How fast clips play: 1 as recorded, 1.25 a quarter faster. The voice keeps its pitch, and <see cref="Position"/>
    /// stays in the clip's own seconds, so its sentence timings still apply.
    /// </summary>
    double Rate { set; }

    /// <summary>Seconds into the clip being played.</summary>
    double Position { get; }

    /// <summary>The clip has played to its end.</summary>
    bool IsEnded { get; }

    /// <summary>Playback failed part-way (a broken file, a lost connection); the service carries on silently.</summary>
    event EventHandler? Failed;
}

/// <summary>No audio at all: every clip runs on the silent clock with timed subtitles.</summary>
public sealed class SilentNarrationAudio : INarrationAudio
{
    public static SilentNarrationAudio Instance { get; } = new();

    public bool Has(string clip) => false;

    public bool Play(string clip, double volume) => false;

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void Stop()
    {
    }

    public double Volume
    {
        set { }
    }

    public double Rate
    {
        set { }
    }

    public double Position => 0;

    public bool IsEnded => true;

    public event EventHandler? Failed
    {
        add { }
        remove { }
    }
}
