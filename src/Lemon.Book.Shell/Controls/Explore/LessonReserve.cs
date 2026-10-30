namespace Lemon.Book.Shell.Controls.Explore;

/// <summary>
/// The room an Explore diagram leaves at the bottom while a walkthrough's subtitle strip sits above its toolbar, so
/// the strip does not cover the picture. The diagram eases towards it frame by frame instead of jumping.
/// </summary>
internal sealed class LessonReserve
{
    /// <summary>DIPs for a two-line subtitle with the step buttons, plus the gap above the toolbar.</summary>
    public const double Height = 120;

    /// <summary>The room to leave now, 0…<see cref="Height"/>.</summary>
    public double Current { get; private set; }

    /// <summary>Moves <see cref="Current"/> towards the target; true while it is still moving.</summary>
    public bool Step(bool lessonActive, double seconds)
    {
        var target = lessonActive ? Height : 0;
        if (Math.Abs(Current - target) < 0.5)
        {
            Current = target;
            return false;
        }

        Current += (target - Current) * Math.Min(1, seconds * 7);
        return true;
    }
}
