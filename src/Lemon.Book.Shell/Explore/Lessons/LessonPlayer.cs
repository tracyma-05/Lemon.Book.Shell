using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lemon.Book.Shell.Explore.Narration;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.Infrastructures.Timing;

namespace Lemon.Book.Shell.Explore.Lessons;

/// <summary>
/// One step of a spoken walkthrough: a narration clip (<c>Narr_*</c> key, also the subtitle), how to set the scene
/// first, what to point at, and optionally something for the viewer to do or a question to answer before going on.
/// </summary>
public sealed record LessonStep(string Key)
{
    /// <summary>Sets the page up for this step (mode, time, options, playback). Runs before the clip starts.</summary>
    public Action? Enter { get; init; }

    /// <summary>What the diagram rings while this step plays; an id the page's diagram knows, or null.</summary>
    public string? Highlight { get; init; }

    /// <summary>"Your turn": after the clip, wait until this is true (the viewer can skip).</summary>
    public Func<bool>? TryIt { get; init; }

    /// <summary>A question: resource keys of the answers to pick from after the clip.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Index into <see cref="Choices"/> of the right answer.</summary>
    public int Answer { get; init; }
}

public enum LessonState
{
    Idle,
    Speaking,
    Paused,
    TryIt,
    Quiz,
    Feedback,
    Done,
}

/// <summary>An answer to a lesson question, lit up once picked.</summary>
public sealed partial class LessonChoice(string key, int index) : ObservableObject
{
    public string Key { get; } = key;

    public int Index { get; } = index;

    public string Text => LocalizationService.Instance.GetString(Key);

    /// <summary>The right answer, shown once any answer is picked.</summary>
    [ObservableProperty]
    private bool _isRight;

    /// <summary>Picked, and wrong.</summary>
    [ObservableProperty]
    private bool _isWrong;

    internal void RefreshText() => OnPropertyChanged(nameof(Text));
}

/// <summary>
/// Runs a chapter's walkthrough (built per view by the chapter) through the shared <see cref="INarrationService"/>: plays
/// each step's clip with subtitles, sets the scene, points at things, waits for "your turn" steps and questions, and
/// pauses when the viewer takes over the picture. The page binds <see cref="Highlight"/> to its diagram and calls
/// <see cref="UserInteracted"/> when the viewer drags or clicks in it.
/// </summary>
public sealed partial class LessonPlayer : ObservableObject, IDisposable
{
    public const string RightKey = "Narr_Common_Right";
    public const string WrongKey = "Narr_Common_Wrong";
    public const string NiceKey = "Narr_Common_Nice";
    public const string RevealKey = "Narr_Common_Reveal";
    public const string MoveOnKey = "Narr_Common_MoveOn";

    private readonly INarrationService _narration;
    private readonly Func<IReadOnlyList<LessonStep>> _build;
    private readonly DispatcherTimer? _timer;
    private readonly BookStopwatch _clock = new();
    private IReadOnlyList<LessonStep> _steps = [];
    private string? _speakingKey;
    private double? _advanceIn;
    private LessonState _pausedFrom;
    private double _lastTick;

    /// <summary>Seconds a question or a "your turn" step has waited for the viewer.</summary>
    private double _waited;

    /// <summary>Paused because the viewer took over the picture (not by the pause button): resumes on its own.</summary>
    private bool _autoPaused;

    /// <summary>Seconds since the viewer last touched the picture while auto-paused.</summary>
    private double _idle;

    /// <param name="build">Builds the steps for the page's current view; called on every start.</param>
    /// <param name="useTimer">False in tests, which call <see cref="Tick"/> themselves.</param>
    public LessonPlayer(INarrationService narration, Func<IReadOnlyList<LessonStep>> build, bool useTimer = true)
    {
        _narration = narration;
        _build = build;
        if (useTimer)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (_, _) => Tick();
        }

        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        narration.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Seconds between one step's clip ending and the next step starting.</summary>
    public double GapSeconds { get; set; } = 0.8;

    /// <summary>A question nobody answers in this many seconds shows its answer and the lesson goes on.</summary>
    public double AnswerSeconds { get; set; } = 5;

    /// <summary>A "your turn" step left alone this long (no touch in the picture) is skipped.</summary>
    public double TryItSeconds { get; set; } = 12;

    /// <summary>After the viewer stops touching the picture (and any clicked explanation ends), the lesson goes on.</summary>
    public double ResumeSeconds { get; set; } = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsPaused), nameof(IsTryIt), nameof(IsQuiz), nameof(IsDone),
        nameof(CanPause), nameof(StateText), nameof(ShowSubtitle), nameof(IsWaiting))]
    private LessonState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    private int _index;

    /// <summary>The sentence being spoken; the whole step while waiting for the viewer.</summary>
    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>What the diagram should ring, or null.</summary>
    [ObservableProperty]
    private string? _highlight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuiz), nameof(ShowSubtitle))]
    private IReadOnlyList<LessonChoice> _choices = [];

    public bool IsActive => State != LessonState.Idle;

    public bool IsPaused => State == LessonState.Paused;

    public bool IsTryIt => State == LessonState.TryIt;

    public bool IsQuiz => Choices.Count > 0;

    public bool IsDone => State == LessonState.Done;

    /// <summary>A question or a task is waiting for the viewer, with <see cref="WaitProgress"/> running down.</summary>
    public bool IsWaiting => State is LessonState.Quiz or LessonState.TryIt;

    /// <summary>How much of the wait is used up, 0 to 1: the answer is shown (or the task skipped) at 1.</summary>
    [ObservableProperty]
    private double _waitProgress;

    public bool CanPause => State is LessonState.Speaking or LessonState.Feedback or LessonState.Paused;

    /// <summary>Subtitles can be turned off in settings, but a question, a task or the end is always shown.</summary>
    public bool ShowSubtitle => _narration.Settings.ShowSubtitles || IsTryIt || IsQuiz || IsDone;

    public int Count => _steps.Count;

    public string StepText => LocalizationService.Instance.Format("Lesson_Step", Math.Min(Index + 1, Count), Count);

    public string StateText => LocalizationService.Instance.GetString(State switch
    {
        LessonState.Paused => "Lesson_State_Paused",
        LessonState.TryIt => "Lesson_State_TryIt",
        LessonState.Quiz => "Lesson_State_Quiz",
        LessonState.Done => "Lesson_State_Done",
        _ => "Lesson_State_Speaking",
    });

    /// <summary>The viewer worked the picture or the page's controls (raised on every <see cref="UserInteracted"/>).</summary>
    public event EventHandler? Interacted;

    /// <summary>True while a step sets the scene, so the page can tell the lesson's changes from the viewer's.</summary>
    public bool IsApplying { get; private set; }

    [RelayCommand]
    private void Toggle()
    {
        if (IsActive) Stop();
        else Start();
    }

    public void Start()
    {
        _steps = _build();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(StepText));
        if (_steps.Count == 0) return;

        _clock.Restart();
        _lastTick = 0;
        _timer?.Start();
        Go(0);
    }

    [RelayCommand]
    public void Stop()
    {
        _timer?.Stop();
        _narration.Stop(this);
        _speakingKey = null;
        _advanceIn = null;
        Choices = [];
        Highlight = null;
        Subtitle = string.Empty;
        State = LessonState.Idle;
    }

    [RelayCommand]
    private void Next()
    {
        if (!IsActive) return;
        if (Index + 1 < _steps.Count) Go(Index + 1);
        else Finish();
    }

    [RelayCommand]
    private void Previous()
    {
        if (!IsActive) return;
        Go(State == LessonState.Done ? _steps.Count - 1 : Math.Max(0, Index - 1));
    }

    [RelayCommand]
    private void Restart()
    {
        if (!IsActive)
        {
            Start();
            return;
        }

        Go(0);
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (State == LessonState.Paused) Resume();
        else if (State is LessonState.Speaking or LessonState.Feedback) Pause(auto: false);
    }

    /// <summary>Skips a "your turn" step or a question.</summary>
    [RelayCommand]
    private void Skip() => Next();

    [RelayCommand]
    private void Answer(LessonChoice? choice)
    {
        if (choice is null || State != LessonState.Quiz) return;

        var right = choice.Index == _steps[Index].Answer;
        foreach (var c in Choices) c.IsRight = c.Index == _steps[Index].Answer;
        choice.IsWrong = !right;
        ResetWait();
        State = LessonState.Feedback;
        Speak(right ? RightKey : WrongKey);
    }

    /// <summary>The viewer dragged or clicked in the picture: a step being spoken pauses so it is not talked over.</summary>
    /// <remarks>It goes on by itself once they leave the picture alone for <see cref="ResumeSeconds"/>; a "your turn"
    /// step gets its full time again.</remarks>
    public void UserInteracted()
    {
        Interacted?.Invoke(this, EventArgs.Empty);
        switch (State)
        {
            case LessonState.Speaking or LessonState.Feedback:
                Pause(auto: true);
                break;
            case LessonState.Paused:
                _idle = 0;
                break;
            case LessonState.TryIt:
                ResetWait();
                break;
        }
    }

    private void Pause(bool auto)
    {
        _pausedFrom = State;
        _autoPaused = auto;
        _idle = 0;
        State = LessonState.Paused;
        if (ReferenceEquals(_narration.CurrentOwner, this)) _narration.Pause();
    }

    private void Resume()
    {
        _autoPaused = false;
        State = _pausedFrom;
        if (_speakingKey is null) return;

        // Something else was played meanwhile (a clicked object's explanation): say the step again from the top.
        if (!ReferenceEquals(_narration.CurrentOwner, this) || _narration.CurrentKey != _speakingKey) Speak(_speakingKey);
        else _narration.Resume();
    }

    private void Go(int index)
    {
        Index = index;
        Choices = [];
        _advanceIn = null;
        _autoPaused = false;
        ResetWait();
        var step = _steps[index];
        IsApplying = true;
        try
        {
            step.Enter?.Invoke();
        }
        finally
        {
            IsApplying = false;
        }

        Highlight = step.Highlight;
        State = LessonState.Speaking;
        Speak(step.Key);
    }

    private void Finish()
    {
        _narration.Stop(this);
        _speakingKey = null;
        _advanceIn = null;
        _autoPaused = false;
        ResetWait();
        Choices = [];
        Highlight = null;
        State = LessonState.Done;
        Subtitle = LocalizationService.Instance.GetString("Lesson_Done_Text");
    }

    private void Speak(string key)
    {
        _speakingKey = key;
        _advanceIn = null;
        _narration.Play(key, this);
        Subtitle = _narration.Clip?.SentenceAt(0) ?? string.Empty;
    }

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        Tick(now - _lastTick);
        _lastTick = now;
    }

    /// <summary>Moves the lesson on by <paramref name="seconds"/>: subtitles, the end of a clip, the gap, a task done.</summary>
    public void Tick(double seconds)
    {
        if (State is LessonState.Idle or LessonState.Done) return;
        var step = _steps[Index];

        if (State == LessonState.Paused)
        {
            if (_autoPaused) TickAutoResume(seconds);
            return;
        }

        if (State == LessonState.Quiz)
        {
            if (Wait(seconds, AnswerSeconds)) RevealAnswer();
            return;
        }

        if (State == LessonState.TryIt)
        {
            if (step.TryIt?.Invoke() != false)
            {
                ResetWait();
                State = LessonState.Feedback;
                Speak(NiceKey);
            }
            else if (Wait(seconds, TryItSeconds))
            {
                ResetWait();
                State = LessonState.Feedback;
                Speak(MoveOnKey);
            }

            return;
        }

        // Speaking or Feedback. Another clip took the channel (the viewer clicked something): wait for it, then go on.
        if (!ReferenceEquals(_narration.CurrentOwner, this))
        {
            Pause(auto: true);
            return;
        }

        if (_advanceIn is { } left)
        {
            _advanceIn = left - seconds;
            if (_advanceIn <= 0) Next();
            return;
        }

        if (_narration.Clip is { } clip) Subtitle = clip.SentenceAt(_narration.Position);
        if (!_narration.IsFinished) return;

        if (State == LessonState.Speaking && step.Choices is { Count: > 0 } choices)
        {
            Choices = choices.Select((key, i) => new LessonChoice(key, i)).ToList();
            State = LessonState.Quiz;
            Subtitle = LocalizationService.Instance.GetString(step.Key);
        }
        else if (State == LessonState.Speaking && step.TryIt is { } done && !done())
        {
            State = LessonState.TryIt;
            Subtitle = LocalizationService.Instance.GetString(step.Key);
        }
        else
        {
            _advanceIn = GapSeconds;
        }
    }

    /// <summary>Counts the wait up; true once <paramref name="limit"/> seconds have gone by.</summary>
    private bool Wait(double seconds, double limit)
    {
        _waited += seconds;
        WaitProgress = Math.Clamp(_waited / limit, 0, 1);
        return _waited >= limit;
    }

    private void ResetWait()
    {
        _waited = 0;
        WaitProgress = 0;
    }

    /// <summary>Nobody answered in time: light up the right answer, say so, and go on as after an answer.</summary>
    private void RevealAnswer()
    {
        var answer = _steps[Index].Answer;
        foreach (var c in Choices) c.IsRight = c.Index == answer;
        ResetWait();
        State = LessonState.Feedback;
        Speak(RevealKey);
    }

    /// <summary>Auto-paused: once a clicked explanation has ended and the picture is left alone, go on.</summary>
    private void TickAutoResume(double seconds)
    {
        var othersPlaying = _narration.CurrentOwner is { } owner && !ReferenceEquals(owner, this) && !_narration.IsFinished;
        _idle = othersPlaying ? 0 : _idle + seconds;
        if (_idle >= ResumeSeconds) Resume();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(StateText));
        foreach (var choice in Choices) choice.RefreshText();

        switch (State)
        {
            case LessonState.Speaking or LessonState.Feedback when _speakingKey is not null && _advanceIn is null:
                Speak(_speakingKey);
                break;
            case LessonState.Paused when _speakingKey is not null:
                // Resume then says it again in the new language.
                _narration.Stop(this);
                Subtitle = LocalizationService.Instance.GetString(_speakingKey);
                break;
            case LessonState.TryIt or LessonState.Quiz:
                Subtitle = LocalizationService.Instance.GetString(_steps[Index].Key);
                break;
            case LessonState.Done:
                Subtitle = LocalizationService.Instance.GetString("Lesson_Done_Text");
                break;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ShowSubtitle));

    public void Dispose()
    {
        Stop();
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
        _narration.SettingsChanged -= OnSettingsChanged;
    }
}
