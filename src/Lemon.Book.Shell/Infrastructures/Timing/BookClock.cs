using System.Diagnostics;

namespace Lemon.Book.Shell.Infrastructures.Timing;

/// <summary>
/// The time the book's animations, walkthroughs and narration run on. Normally the system's high-resolution clock;
/// while a video is recorded it is held and stepped one frame at a time, so a frame that is slow to render still moves
/// everything on by exactly one frame. Read through <see cref="BookStopwatch"/>.
/// </summary>
public static class BookClock
{
    /// <summary>Added to the system clock, so time carries on smoothly when stepping stops.</summary>
    private static double s_offset;

    /// <summary>The held time while stepping, else null.</summary>
    private static double? s_held;

    /// <summary>Seconds on the book's clock (an arbitrary origin; only differences mean anything).</summary>
    public static double Now => s_held ?? System + s_offset;

    /// <summary>True while time only moves when <see cref="Advance"/> is called.</summary>
    public static bool IsStepped => s_held is not null;

    private static double System => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Holds time where it is; from now on only <see cref="Advance"/> moves it.</summary>
    public static void BeginStepping() => s_held ??= Now;

    /// <summary>Moves held time on by <paramref name="seconds"/>.</summary>
    public static void Advance(double seconds)
    {
        if (s_held is null) throw new InvalidOperationException("The book clock is not being stepped.");
        s_held += seconds;
    }

    /// <summary>Lets time run again, from where the held time had got to.</summary>
    public static void EndStepping()
    {
        if (s_held is not { } held) return;
        s_offset = held - System;
        s_held = null;
    }
}

/// <summary>A <see cref="Stopwatch"/> on <see cref="BookClock"/>: the same members, for the ones the book uses.</summary>
public sealed class BookStopwatch
{
    private double _startedAt;
    private double _banked;

    public bool IsRunning { get; private set; }

    public TimeSpan Elapsed => TimeSpan.FromSeconds(_banked + (IsRunning ? BookClock.Now - _startedAt : 0));

    public static BookStopwatch StartNew()
    {
        var watch = new BookStopwatch();
        watch.Start();
        return watch;
    }

    public void Start()
    {
        if (IsRunning) return;
        _startedAt = BookClock.Now;
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _banked += BookClock.Now - _startedAt;
        IsRunning = false;
    }

    public void Reset()
    {
        _banked = 0;
        IsRunning = false;
    }

    public void Restart()
    {
        Reset();
        Start();
    }
}
