using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Lemon.Book.Shell.Controls.Explore;

/// <summary>What a page shows, told when its views come on and leave the screen (to load and drop pictures).</summary>
public interface IChapterContent
{
    /// <summary>A view of the page came on screen (it is shown, or about to be turned to).</summary>
    void ViewAttached();

    /// <summary>
    /// A view of the page left the screen. Once none is left the reader has turned away: what it loaded can go, and
    /// anything being read aloud stops rather than talking over the next page.
    /// </summary>
    void ViewDetached();
}

/// <summary>
/// A chapter that takes keys while its spread is open: digits typed into a practice sum, Space to play or pause. The
/// book asks it before turning pages, so it must leave the arrow keys alone.
/// </summary>
public interface IChapterKeys
{
    /// <summary>Handles <paramref name="key"/> and returns true, or returns false to leave it to the book.</summary>
    bool OnKey(Key key);
}

/// <summary>A page whose content wants to know when it is on screen.</summary>
public interface IChapterPage
{
    IChapterContent Content { get; }
}

/// <summary>Base of the page views: reports them coming on and off screen, and trims their margins on a phone.</summary>
public abstract class ChapterPageView : UserControl
{
    /// <summary>A page narrower than this (a phone's) gets slimmer margins than the book's.</summary>
    internal const double NarrowPageWidth = 520;

    /// <summary>The slimmest margins a narrow page keeps on each side: left, top, right, bottom.</summary>
    private static readonly Thickness NarrowMargin = new(12, 14, 12, 10);

    private IChapterContent? _shown;
    private Thickness? _bookMargin;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == ContentProperty)
        {
            FitMargin();
        }
    }

    /// <summary>
    /// The page views set the book's generous margins on their root panel. On a phone they would take a sixth of the
    /// width from the diagram, so a narrow page trims each to <see cref="NarrowMargin"/>, and a wide one puts them back.
    /// </summary>
    private void FitMargin()
    {
        if (Content is not Control root || Bounds.Width <= 0)
        {
            return;
        }

        _bookMargin ??= root.Margin;
        var book = _bookMargin.Value;
        root.Margin = Bounds.Width >= NarrowPageWidth
            ? book
            : new Thickness(
                Math.Min(book.Left, NarrowMargin.Left),
                Math.Min(book.Top, NarrowMargin.Top),
                Math.Min(book.Right, NarrowMargin.Right),
                Math.Min(book.Bottom, NarrowMargin.Bottom));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Show((DataContext as IChapterPage)?.Content);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Show(null);
    }

    /// <summary>
    /// Data templates recycle their views: turning from one story page to the next keeps this view and gives it the
    /// next page. That is the old page leaving the screen and the new one coming on.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null)
        {
            Show((DataContext as IChapterPage)?.Content);
        }
    }

    private void Show(IChapterContent? content)
    {
        if (ReferenceEquals(content, _shown))
        {
            return;
        }

        _shown?.ViewDetached();
        _shown = content;
        _shown?.ViewAttached();
    }
}
