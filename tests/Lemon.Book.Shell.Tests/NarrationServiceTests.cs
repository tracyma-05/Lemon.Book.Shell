using System.Globalization;
using Lemon.Book.Shell.Explore.Narration;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.Infrastructures.Settings;
using Xunit;

namespace Lemon.Book.Shell.Tests;

public sealed class NarrationServiceTests
{
    private const string Manifest = """
        {
          "en": {
            "voice": "test-voice", "rate": "-10%", "pitch": "+0Hz",
            "clips": {
              "Narr_Seasons_Obj_Sun": { "hash": "abc", "duration": 4.5, "sentences": [[0.2, 2.0, "First."], [2.0, 4.3, "Second."]] }
            }
          }
        }
        """;

    [Fact]
    public void WithoutAudio_AClipRunsOnTheSilentClock_WithTimingsFromItsText()
    {
        var service = new NarrationService(new MemorySettingsStore(), NarrationManifest.Empty, SilentNarrationAudio.Instance);

        var clip = service.Play("Narr_Seasons_Obj_Sun", this);

        Assert.False(clip.HasAudio);
        Assert.NotEmpty(clip.Sentences);
        Assert.True(clip.Duration > 1);
        Assert.False(service.IsFinished);
        Assert.Same(this, service.CurrentOwner);
    }

    [Fact]
    public void TheManifest_GivesTheClipItsRecordedTimings()
    {
        WithCulture("en", () =>
        {
            var audio = new FakeAudio();
            var service = new NarrationService(new MemorySettingsStore(), NarrationManifest.Parse(Manifest), audio);

            var clip = service.Play("Narr_Seasons_Obj_Sun", this);

            Assert.True(clip.HasAudio);
            Assert.Equal(4.5, clip.Duration);
            Assert.Equal("Second.", clip.SentenceAt(3));
            Assert.Equal("en/Narr_Seasons_Obj_Sun.abc.mp3", audio.Playing);
        });
    }

    [Fact]
    public void ABrokenManifest_MeansSubtitlesOnly()
    {
        var manifest = NarrationManifest.Parse("{ not json");

        Assert.Null(manifest.Culture("en"));
    }

    [Fact]
    public void AudioFailingPartWay_CarriesOnSilently()
    {
        WithCulture("en", () =>
        {
            var audio = new FakeAudio();
            var service = new NarrationService(new MemorySettingsStore(), NarrationManifest.Parse(Manifest), audio);
            service.Play("Narr_Seasons_Obj_Sun", this);

            audio.Fail();

            Assert.Null(audio.Playing);
            Assert.False(service.IsFinished);
            Assert.Equal("Narr_Seasons_Obj_Sun", service.CurrentKey);
        });
    }

    [Fact]
    public void Settings_AreRemembered()
    {
        var store = new MemorySettingsStore();
        var service = new NarrationService(store, NarrationManifest.Empty, SilentNarrationAudio.Instance);

        service.Update(service.Settings with { ShowSubtitles = false, Volume = 3 });

        var reloaded = new NarrationService(store, NarrationManifest.Empty, SilentNarrationAudio.Instance);
        Assert.False(reloaded.Settings.ShowSubtitles);
        Assert.Equal(1, reloaded.Settings.Volume);
    }

    [Fact]
    public void Speed_IsRemembered_KeptInRange_AndPassedToThePlayer()
    {
        var store = new MemorySettingsStore();
        var audio = new FakeAudio();
        var service = new NarrationService(store, NarrationManifest.Empty, audio);
        Assert.Equal(1, audio.Rate);

        service.Update(service.Settings with { Speed = 1.25 });
        Assert.Equal(1.25, audio.Rate);

        service.Update(service.Settings with { Speed = 9 });
        Assert.Equal(2, service.Settings.Speed);

        var reloaded = new NarrationService(store, NarrationManifest.Empty, new FakeAudio());
        Assert.Equal(2, reloaded.Settings.Speed);
    }

    [Fact]
    public void WithoutAudio_TheSilentClock_RunsAtTheReadersSpeed()
    {
        var service = new NarrationService(new MemorySettingsStore(), NarrationManifest.Empty, SilentNarrationAudio.Instance);
        service.Update(service.Settings with { Speed = 2 });
        service.Play("Narr_Seasons_Obj_Sun", this);

        Thread.Sleep(300);
        var fast = service.Position;

        // Back to 1x: the clock keeps what it has covered and goes on from there, more slowly.
        service.Update(service.Settings with { Speed = 1 });
        Assert.True(service.Position >= fast * 0.95, $"{service.Position} after {fast}");
        Thread.Sleep(300);

        Assert.InRange(fast, 0.5, 1.2);
        Assert.InRange(service.Position - fast, 0.25, 0.7);
    }

    [Fact]
    public void Stop_WithAnotherOwner_LeavesTheClipPlaying()
    {
        var service = new NarrationService(new MemorySettingsStore(), NarrationManifest.Empty, SilentNarrationAudio.Instance);
        service.Play("Narr_Seasons_Obj_Sun", this);

        service.Stop(new object());
        Assert.NotNull(service.Clip);

        service.Stop(this);
        Assert.Null(service.Clip);
    }

    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("en", "en")]
    [InlineData("fr", "en")]
    public void CultureFolder_MapsAnyChineseToZhCn(string culture, string folder) =>
        Assert.Equal(folder, NarrationService.CultureFolder(culture));

    private static void WithCulture(string name, Action action)
    {
        var localization = LocalizationService.Instance;
        var original = localization.CurrentCulture;
        localization.SetCulture(CultureInfo.GetCultureInfo(name));
        try
        {
            action();
        }
        finally
        {
            localization.SetCulture(original);
        }
    }

    private sealed class FakeAudio : INarrationAudio
    {
        public string? Playing { get; private set; }

        public bool Has(string clip) => true;

        public bool Play(string clip, double volume)
        {
            Playing = clip;
            return true;
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop() => Playing = null;

        public double Volume
        {
            set { }
        }

        public double Rate { get; set; }

        public double Position => 0.5;

        public bool IsEnded => false;

        public event EventHandler? Failed;

        public void Fail() => Failed?.Invoke(this, EventArgs.Empty);
    }
}
