using Avalonia.Headless.XUnit;
using Lemon.Book.Shell.Explore.Lessons;
using Lemon.Book.Shell.Infrastructures.Settings;
using Xunit;
using FakeNarration = Lemon.Book.Shell.Tests.LessonPlayerTests.FakeNarration;

namespace Lemon.Book.Shell.Tests;

/// <summary>
/// Practice goes on without clicks: an open question shows its answer after the book's wait (unless the reader turned
/// that off), a finished one makes way for the next.
/// </summary>
public sealed class PracticeAutopilotTests
{
    private sealed class Probe
    {
        public bool Finished { get; set; }

        public int Reveals { get; set; }

        public int Nexts { get; set; }
    }

    private static (PracticeAutopilot Pilot, Probe Probe, LessonPlayer Lesson) Make()
    {
        var probe = new Probe();
        var lesson = new LessonPlayer(new FakeNarration(), () => [new LessonStep("Narr_A")], useTimer: false);
        var pilot = new PracticeAutopilot(lesson, () => true, () => probe.Finished,
            () => { probe.Reveals++; probe.Finished = true; },
            () => { probe.Nexts++; probe.Finished = false; }, useTimer: false);
        return (pilot, probe, lesson);
    }

    [AvaloniaFact]
    public void An_open_question_shows_its_answer_after_the_books_wait_then_the_next_one_comes()
    {
        var wait = PracticeAutopilot.RevealAfterSeconds;
        try
        {
            PracticeAutopilot.RevealAfterSeconds = 20;
            var (pilot, probe, _) = Make();

            pilot.Tick(19.9);
            Assert.Equal(0, probe.Reveals);
            pilot.Tick(0.2);
            Assert.Equal(1, probe.Reveals);

            pilot.Tick(pilot.NextSeconds + 0.1);
            Assert.Equal(1, probe.Nexts);
        }
        finally
        {
            PracticeAutopilot.RevealAfterSeconds = wait;
        }
    }

    [AvaloniaFact]
    public void Typing_restarts_the_wait()
    {
        var (pilot, probe, lesson) = Make();

        pilot.Tick(pilot.AnswerSeconds - 1);
        lesson.UserInteracted();
        pilot.Tick(pilot.AnswerSeconds - 1);
        pilot.Touch();
        pilot.Tick(pilot.AnswerSeconds - 1);

        Assert.Equal(0, probe.Reveals);
    }

    [AvaloniaFact]
    public void With_revealing_off_an_open_question_waits_but_an_answered_one_still_moves_on()
    {
        var store = new MemorySettingsStore();
        try
        {
            PracticeAutopilot.SetRevealsAnswers(store, false);
            Assert.Equal(bool.FalseString, store.Get(SettingKeys.PracticeRevealsAnswers));

            var (pilot, probe, _) = Make();
            pilot.Tick(600);
            Assert.Equal(0, probe.Reveals);

            probe.Finished = true;
            pilot.Tick(pilot.NextSeconds + 0.1);
            Assert.Equal(1, probe.Nexts);

            // The choice comes back with the settings; on is the default and is not stored.
            PracticeAutopilot.RevealsAnswers = true;
            PracticeAutopilot.Load(store);
            Assert.False(PracticeAutopilot.RevealsAnswers);
            PracticeAutopilot.SetRevealsAnswers(store, true);
            Assert.Null(store.Get(SettingKeys.PracticeRevealsAnswers));
            PracticeAutopilot.Load(new MemorySettingsStore());
            Assert.True(PracticeAutopilot.RevealsAnswers);
        }
        finally
        {
            PracticeAutopilot.RevealsAnswers = true;
        }
    }
}
