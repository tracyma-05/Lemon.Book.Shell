using Avalonia.Threading;
using Lemon.Book.Shell.Infrastructures.Settings;
using Lemon.Book.Shell.Infrastructures.Timing;

namespace Lemon.Book.Shell.Explore.Lessons;

/// <summary>
/// Keeps a practice page going without clicks: a question left unanswered for <see cref="AnswerSeconds"/> shows its
/// answer, and a finished question (answered or shown) is followed by a new one after <see cref="NextSeconds"/>. Every key
/// or click the page reports to its walkthrough (<see cref="LessonPlayer.UserInteracted"/>) restarts the wait, so typing
/// a long answer is not cut off; other input calls <see cref="Touch"/>. It stands aside while the page's walkthrough
/// runs, which sets its own questions. With <see cref="RevealsAnswers"/> off (the reader's choice in the book's settings),
/// an open question waits for the child however long it takes.
/// </summary>
public sealed class PracticeAutopilot : IDisposable
{
    private readonly LessonPlayer _lesson;
    private readonly Func<bool> _isPracticing;
    private readonly Func<bool> _isFinished;
    private readonly Action? _reveal;
    private readonly Action _next;
    private readonly DispatcherTimer? _timer;
    private readonly BookStopwatch _clock = new();
    private double _lastTick;
    private double _idle;
    private double _done;

    /// <param name="lesson">The page's walkthrough; nothing happens while it is running.</param>
    /// <param name="isPracticing">The practice view is shown.</param>
    /// <param name="isFinished">The current question is answered or its answer shown.</param>
    /// <param name="reveal">Shows the current question's answer (and counts it as not solved alone); null for work with
    /// no answer to show (tracing a character), which waits as long as it takes.</param>
    /// <param name="next">Sets a new question.</param>
    /// <param name="useTimer">False in tests, which call <see cref="Tick"/> themselves.</param>
    public PracticeAutopilot(LessonPlayer lesson, Func<bool> isPracticing, Func<bool> isFinished, Action? reveal, Action next, bool useTimer = true)
    {
        _lesson = lesson;
        _isPracticing = isPracticing;
        _isFinished = isFinished;
        _reveal = reveal;
        _next = next;
        lesson.Interacted += OnInteracted;
        if (!useTimer) return;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            var now = _clock.Elapsed.TotalSeconds;
            Tick(now - _lastTick);
            _lastTick = now;
        };
        _clock.Start();
        _timer.Start();
    }

    /// <summary>
    /// The reader's choice for every practice page: an open question left alone shows its answer. Off, it waits for the
    /// child. Read from the settings with <see cref="Load"/>, changed with <see cref="SetRevealsAnswers"/>.
    /// </summary>
    public static bool RevealsAnswers { get; set; } = true;

    /// <summary>
    /// The book's wait before an open question shows its answer, for every practice page: a maths book gives the child
    /// long enough to work a sum out in their head.
    /// </summary>
    public static double RevealAfterSeconds { get; set; } = 5;

    /// <summary>Seconds without a key or click on an open question before its answer is shown.</summary>
    internal double AnswerSeconds { get; set; } = RevealAfterSeconds;

    /// <summary>The reader's choice (<see cref="RevealsAnswers"/>) from the settings, when the book is built over them.</summary>
    public static void Load(ISettingsStore settings) =>
        RevealsAnswers = settings.Get(SettingKeys.PracticeRevealsAnswers) != bool.FalseString;

    /// <summary>The reader changed <see cref="RevealsAnswers"/>: kept in the settings (only when off; on is the default).</summary>
    public static void SetRevealsAnswers(ISettingsStore settings, bool value)
    {
        RevealsAnswers = value;
        settings.Set(SettingKeys.PracticeRevealsAnswers, value ? null : bool.FalseString);
    }

    /// <summary>Seconds a finished question stays up (the praise, or the shown answer) before the next one.</summary>
    internal double NextSeconds { get; set; } = 3;

    /// <summary>The child did something on the question: the answer waits another <see cref="AnswerSeconds"/>.</summary>
    public void Touch() => _idle = 0;

    /// <summary>A new question is up: both waits start again.</summary>
    public void Reset()
    {
        _idle = 0;
        _done = 0;
    }

    public void Tick(double seconds)
    {
        if (!_isPracticing() || (_lesson.IsActive && !_lesson.IsDone))
        {
            Reset();
            return;
        }

        if (_isFinished())
        {
            _done += seconds;
            if (_done < NextSeconds) return;
            Reset();
            _next();
            return;
        }

        _done = 0;
        if (_reveal is null || !RevealsAnswers) return;
        _idle += seconds;
        if (_idle < AnswerSeconds) return;
        _idle = 0;
        _reveal();
    }

    private void OnInteracted(object? sender, EventArgs e) => Touch();

    public void Dispose()
    {
        _timer?.Stop();
        _lesson.Interacted -= OnInteracted;
    }
}
