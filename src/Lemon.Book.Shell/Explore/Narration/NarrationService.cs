using System.Diagnostics;
using System.Text.Json;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.Infrastructures.Settings;
using Lemon.Book.Shell.Infrastructures.Timing;

namespace Lemon.Book.Shell.Explore.Narration;

/// <summary>The reader's narration preferences, saved as one JSON setting.</summary>
public sealed record NarrationSettings
{
    /// <summary>Speak the clips; when off, walkthroughs still run with subtitles at a reading pace.</summary>
    public bool VoiceEnabled { get; init; } = true;

    public double Volume { get; init; } = 0.9;

    public bool ShowSubtitles { get; init; } = true;

    /// <summary>Clicking a thing in a picture reads its explanation aloud (it is always shown).</summary>
    public bool SpeakOnClick { get; init; } = true;

    /// <summary>How fast the voice reads (and the subtitles run without it): 1 as recorded; the pitch is kept.</summary>
    public double Speed { get; init; } = 1;

    /// <summary>The speeds offered in Settings.</summary>
    public static IReadOnlyList<double> Speeds { get; } = [0.75, 1, 1.25, 1.5];

    internal const double MinSpeed = 0.5;

    internal const double MaxSpeed = 2;
}

/// <summary>
/// The book's one narration channel: plays a clip (<c>Narr_*</c> key) in the current language, one at a time.
/// Audio is pre-rendered (<see cref="NarrationManifest"/>); a clip without audio runs on a silent clock with estimated
/// timings so subtitles and walkthroughs behave the same.
/// </summary>
public interface INarrationService
{
    NarrationSettings Settings { get; }

    event EventHandler? SettingsChanged;

    void Update(NarrationSettings settings);

    /// <summary>
    /// Starts <paramref name="key"/>, replacing whatever was playing. <paramref name="owner"/> is who asked, for
    /// <see cref="Stop"/>. <paramref name="text"/> is what the clip says when it is not a resource string (a story
    /// sentence), to time it without a recording.
    /// </summary>
    NarrationClip Play(string key, object owner, string? text = null);

    /// <summary>Whether <paramref name="key"/> has recorded audio here, so playing it is heard (not just timed).</summary>
    bool HasAudio(string key) => false;

    /// <summary>
    /// The words <paramref name="key"/>'s voice says, in order (小 / 狐狸 / 在哪儿), from its recording's word timings;
    /// none when it has none. A story page keeps each word on one line.
    /// </summary>
    IReadOnlyList<string> WordsOf(string key) => [];

    void Pause();

    void Resume();

    /// <summary>Stops the current clip; with an owner, only if that owner started it.</summary>
    void Stop(object? owner = null);

    /// <summary>The clip playing now (its timings follow the audio), or null.</summary>
    NarrationClip? Clip { get; }

    string? CurrentKey { get; }

    object? CurrentOwner { get; }

    bool IsPaused { get; }

    /// <summary>Seconds into the current clip.</summary>
    double Position { get; }

    /// <summary>The current clip has played to the end (also true when nothing is playing).</summary>
    bool IsFinished { get; }
}

public sealed class NarrationService : INarrationService
{
    private readonly ISettingsStore _store;
    private readonly NarrationManifest _manifest;
    private readonly INarrationAudio _audio;
    private readonly BookStopwatch _silent = new();

    /// <summary>Clip seconds the silent clock had covered before it was last restarted (a speed change, a switch from audio).</summary>
    private double _silentBase;
    private NarrationClip? _clip;
    private bool _playingAudio;

    public NarrationService(ISettingsStore store, NarrationManifest manifest, INarrationAudio audio)
    {
        _store = store;
        _manifest = manifest;
        _audio = audio;
        Settings = Load(store);
        Settings = Settings with { Speed = Math.Clamp(Settings.Speed, NarrationSettings.MinSpeed, NarrationSettings.MaxSpeed) };
        audio.Rate = Settings.Speed;
        audio.Failed += OnAudioFailed;
    }

    public NarrationSettings Settings { get; private set; }

    public event EventHandler? SettingsChanged;

    public NarrationClip? Clip => _clip;

    public string? CurrentKey => _clip?.Key;

    public object? CurrentOwner { get; private set; }

    public bool IsPaused { get; private set; }

    public double Position => _clip is null ? 0 : _playingAudio ? _audio.Position : SilentPosition;

    public bool IsFinished => _clip is null
        || (_playingAudio ? _audio.IsEnded || Position > _clip.Duration + 3 : SilentPosition >= _clip.Duration);

    /// <summary>Where the silent clock is in the clip: it runs at the reader's speed, as the voice would.</summary>
    private double SilentPosition => _silentBase + (_silent.Elapsed.TotalSeconds * Settings.Speed);

    public void Update(NarrationSettings settings)
    {
        if (settings == Settings)
        {
            return;
        }

        var voiceTurnedOff = Settings.VoiceEnabled && !settings.VoiceEnabled;

        // The silent clock keeps the clip seconds it has covered at the old speed before running at the new one.
        var silentSoFar = SilentPosition;
        Settings = settings with
        {
            Volume = Math.Clamp(settings.Volume, 0, 1),
            Speed = Math.Clamp(settings.Speed, NarrationSettings.MinSpeed, NarrationSettings.MaxSpeed),
        };
        _store.Set(SettingKeys.Narration, JsonSerializer.Serialize(Settings, BookJson.Default.NarrationSettings));
        _audio.Volume = Settings.Volume;
        _audio.Rate = Settings.Speed;
        if (_clip is not null && !_playingAudio)
        {
            RestartSilent(silentSoFar);
        }

        // Carry on where the voice was, without it.
        if (voiceTurnedOff && _playingAudio)
        {
            SwitchToSilent(Position);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasAudio(string key)
    {
        var folder = CultureFolder(LocalizationService.Instance.CurrentCulture.Name);
        var entry = _manifest.Culture(folder)?.Clips.GetValueOrDefault(key);
        return entry is not null && _audio.Has(NarrationManifest.ClipPath(folder, key, entry.Hash));
    }

    public IReadOnlyList<string> WordsOf(string key)
    {
        var folder = CultureFolder(LocalizationService.Instance.CurrentCulture.Name);
        var entry = _manifest.Culture(folder)?.Clips.GetValueOrDefault(key);
        return entry is null ? [] : entry.Words.Select(w => w.Text).ToArray();
    }

    public NarrationClip Play(string key, object owner, string? text = null)
    {
        StopPlayback();
        var culture = LocalizationService.Instance.CurrentCulture;
        var folder = CultureFolder(culture.Name);
        var entry = _manifest.Culture(folder)?.Clips.GetValueOrDefault(key);
        var path = entry is null ? null : NarrationManifest.ClipPath(folder, key, entry.Hash);
        var hasAudio = path is not null && _audio.Has(path);
        _clip = entry is null
            ? NarrationClip.Estimate(key, text ?? LocalizationService.Instance.GetString(key), culture)
            : new NarrationClip(key, entry.Duration, entry.Sentences, hasAudio, entry.Words);
        CurrentOwner = owner;
        IsPaused = false;

        _playingAudio = hasAudio && Settings.VoiceEnabled && _audio.Play(path!, Settings.Volume);
        if (!_playingAudio)
        {
            RestartSilent(0);
        }

        return _clip;
    }

    public void Pause()
    {
        if (_clip is null || IsPaused)
        {
            return;
        }

        IsPaused = true;
        if (_playingAudio)
        {
            _audio.Pause();
        }
        else
        {
            _silent.Stop();
        }
    }

    public void Resume()
    {
        if (_clip is null || !IsPaused)
        {
            return;
        }

        IsPaused = false;
        if (_playingAudio)
        {
            _audio.Resume();
        }
        else
        {
            _silent.Start();
        }
    }

    public void Stop(object? owner = null)
    {
        if (owner is not null && !ReferenceEquals(owner, CurrentOwner))
        {
            return;
        }

        StopPlayback();
        _clip = null;
        CurrentOwner = null;
        IsPaused = false;
    }

    /// <summary>The audio folder for a culture: zh-CN for any Chinese, otherwise English.</summary>
    internal static string CultureFolder(string culture) =>
        culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";

    private static NarrationSettings Load(ISettingsStore store)
    {
        try
        {
            return store.Get(SettingKeys.Narration) is { } json
                ? JsonSerializer.Deserialize(json, BookJson.Default.NarrationSettings) ?? new NarrationSettings()
                : new NarrationSettings();
        }
        catch (JsonException)
        {
            return new NarrationSettings();
        }
    }

    private void StopPlayback()
    {
        if (_playingAudio)
        {
            _audio.Stop();
        }

        _playingAudio = false;
        _silent.Reset();
        _silentBase = 0;
    }

    private void OnAudioFailed(object? sender, EventArgs e)
    {
        Trace.TraceWarning($"Narration audio {_clip?.Key} failed to play; showing subtitles only.");
        if (_playingAudio)
        {
            SwitchToSilent(0);
        }
    }

    private void SwitchToSilent(double seconds)
    {
        _audio.Stop();
        _playingAudio = false;
        RestartSilent(seconds);
    }

    /// <summary>Starts the silent clock again from <paramref name="seconds"/> into the clip (stopped while paused).</summary>
    private void RestartSilent(double seconds)
    {
        _silentBase = seconds;
        _silent.Restart();
        if (IsPaused)
        {
            _silent.Stop();
        }
    }
}
