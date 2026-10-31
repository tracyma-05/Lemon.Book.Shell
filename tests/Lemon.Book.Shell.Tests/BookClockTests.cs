using Lemon.Book.Shell.Infrastructures.Timing;
using Xunit;

namespace Lemon.Book.Shell.Tests;

/// <summary>The book's clock, held and stepped for a video recording, and its stopwatch.</summary>
public sealed class BookClockTests
{
    [Fact]
    public void WhileStepped_TimeMovesOnlyWhenAdvanced()
    {
        BookClock.BeginStepping();
        try
        {
            var watch = BookStopwatch.StartNew();
            Thread.Sleep(20);
            Assert.Equal(TimeSpan.Zero, watch.Elapsed);

            BookClock.Advance(0.5);
            Assert.Equal(0.5, watch.Elapsed.TotalSeconds, 9);
        }
        finally
        {
            BookClock.EndStepping();
        }
    }

    [Fact]
    public void AfterStepping_TimeCarriesOnFromWhereItWas()
    {
        BookClock.BeginStepping();
        var held = BookClock.Now;
        BookClock.Advance(100);
        BookClock.EndStepping();

        Assert.False(BookClock.IsStepped);
        Assert.InRange(BookClock.Now - held, 100, 101);
    }

    [Fact]
    public void TheStopwatch_StopsResetsAndRestarts_LikeStopwatch()
    {
        BookClock.BeginStepping();
        try
        {
            var watch = new BookStopwatch();
            BookClock.Advance(1);
            Assert.Equal(0, watch.Elapsed.TotalSeconds);

            watch.Start();
            BookClock.Advance(2);
            watch.Stop();
            BookClock.Advance(5);
            Assert.Equal(2, watch.Elapsed.TotalSeconds, 9);
            Assert.False(watch.IsRunning);

            watch.Start();
            BookClock.Advance(1);
            Assert.Equal(3, watch.Elapsed.TotalSeconds, 9);

            watch.Restart();
            BookClock.Advance(0.25);
            Assert.Equal(0.25, watch.Elapsed.TotalSeconds, 9);

            watch.Reset();
            Assert.Equal(0, watch.Elapsed.TotalSeconds);
            Assert.False(watch.IsRunning);
        }
        finally
        {
            BookClock.EndStepping();
        }
    }

    [Fact]
    public void Advancing_WithoutStepping_Throws() =>
        Assert.Throws<InvalidOperationException>(() => BookClock.Advance(1));
}
