using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Lemon.Book.Shell.Contents;

namespace Lemon.Book.Shell.Controls;

/// <summary>
/// Shows a list of pages as a book: two-page spreads on a wide window, one page on a narrow one, with pages
/// that turn by dragging a page's outer edge, a click on it, or the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Pages are live: each is a <see cref="ContentControl"/> whose content is a page view model resolved through
/// the data templates in scope, so diagrams keep animating and stay clickable while the book is at rest.
/// </para>
/// <para>
/// While a page turns, both spreads are rendered to bitmaps and an overlay draws the turn from those (see
/// <see cref="PageCurlGeometry"/>); the target spread is laid out underneath in a second pair of presenters, so
/// when the turn ends the two pairs swap and nothing has to be rebuilt.
/// </para>
/// <para>
/// "Position" below is the unit the current mode moves in: a spread number in spread mode
/// (see <see cref="SpreadLayout"/>), a page index in single-page mode.
/// </para>
/// </remarks>
public sealed class BookView : Control
{
    public static readonly StyledProperty<IReadOnlyList<object>?> PagesProperty =
        AvaloniaProperty.Register<BookView, IReadOnlyList<object>?>(nameof(Pages));

    /// <summary>The reading page. Set it to turn to a page; turning by hand writes it back.</summary>
    public static readonly StyledProperty<int> PageIndexProperty =
        AvaloniaProperty.Register<BookView, int>(nameof(PageIndex), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>False shows the target page at once instead of turning to it (reduced motion).</summary>
    /// <summary>The furthest page the reader may turn to (the books open in order); the last page if not set.</summary>
    public static readonly StyledProperty<int> MaxPageIndexProperty =
        AvaloniaProperty.Register<BookView, int>(nameof(MaxPageIndex), int.MaxValue);

    public static readonly StyledProperty<bool> AnimatesTurnsProperty =
        AvaloniaProperty.Register<BookView, bool>(nameof(AnimatesTurns), true);

    /// <summary>Pages with a bookmark; each gets a ribbon at its top outer corner.</summary>
    public static readonly StyledProperty<IReadOnlyCollection<int>?> BookmarkedPagesProperty =
        AvaloniaProperty.Register<BookView, IReadOnlyCollection<int>?>(nameof(BookmarkedPages));

    /// <summary>Narrowest window that still shows two pages side by side.</summary>
    internal const double MinSpreadWidth = 860;

    /// <summary>Width of the strip along a page's outer edge that grabs the page instead of its content.</summary>
    public const double GrabStripWidth = 56;

    private const double DragThreshold = 6;

    /// <summary>A drag this far (a share of the page's width) towards the turn finishes it when let go.</summary>
    private const double TurnDistance = 0.25;

    /// <summary>A flick this fast (pixels per millisecond) and at least <see cref="FlickDistance"/> long finishes the turn.</summary>
    private const double FlickSpeed = 0.35;

    private const double FlickDistance = 24;
    private static readonly TimeSpan TurnDuration = TimeSpan.FromMilliseconds(650);

    private static readonly IBrush TableBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#4A3527"), 0), new GradientStop(Color.Parse("#2A1D15"), 1) },
    }.ToImmutable();

    // Defaults for the theme's BookPaperBrush, BookPageEdgeBrush, BookTableBrush and AccentBrush (App.axaml).
    private static readonly IBrush PaperBrush = new ImmutableSolidColorBrush(Color.Parse("#FBF7EE"));
    private static readonly IBrush PageEdgeBrush = new ImmutableSolidColorBrush(Color.Parse("#D9D0BE"));
    private static readonly IBrush RibbonBrush = new ImmutableSolidColorBrush(Color.Parse("#B5532E"));
    private static readonly IBrush RibbonShade = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));

    private static readonly BoxShadows BookShadow = new(new BoxShadow
    {
        OffsetY = 10,
        Blur = 30,
        Color = Color.FromArgb(0x90, 0, 0, 0),
    });

    private static readonly IBrush SpineShade = HorizontalBand(
        (Color.FromArgb(0, 0, 0, 0), 0), (Color.FromArgb(0x38, 0, 0, 0), 0.5), (Color.FromArgb(0, 0, 0, 0), 1));

    private static readonly IBrush UncoveredShade = HorizontalBand(
        (Color.FromArgb(0x70, 0, 0, 0), 0), (Color.FromArgb(0, 0, 0, 0), 1));

    private static readonly IBrush FlapShade = HorizontalBand(
        (Color.FromArgb(0x50, 0, 0, 0), 0), (Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.35), (Color.FromArgb(0, 0, 0, 0), 1));

    private ContentControl[] _live;
    private ContentControl[] _staging;
    private readonly TurnOverlay _overlay;

    private bool _isSpread;
    private int _shown;
    private bool _writingPageIndex;
    private Turn? _turn;
    private int? _queuedTarget;
    private Press? _press;

    public BookView()
    {
        Focusable = true;
        ClipToBounds = true;

        _live = [CreatePresenter(), CreatePresenter()];
        _staging = [CreatePresenter(), CreatePresenter()];
        _overlay = new TurnOverlay(this) { IsHitTestVisible = false, ZIndex = 2 };

        foreach (var presenter in _live.Concat(_staging))
        {
            LogicalChildren.Add(presenter);
            VisualChildren.Add(presenter);
        }

        VisualChildren.Add(_overlay);
        SetLayers();

        // Tunnelling, so a grab on a page edge wins over whatever the page shows there, but for its buttons.
        AddHandler(PointerPressedEvent, OnPagePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // The paper, the table and the ribbons follow the light or dark theme and the accent colour.
        ActualThemeVariantChanged += (_, _) => OnThemeChanged();
        ResourcesChanged += (_, _) => OnThemeChanged();
    }

    public IReadOnlyCollection<int>? BookmarkedPages
    {
        get => GetValue(BookmarkedPagesProperty);
        set => SetValue(BookmarkedPagesProperty, value);
    }

    private IBrush Paper => Themed("BookPaperBrush") ?? PaperBrush;

    private IBrush Table => Themed("BookTableBrush") ?? TableBrush;

    private IPen PageEdgePen => new ImmutablePen((Themed("BookPageEdgeBrush") ?? PageEdgeBrush).ToImmutable(), 1);

    private IBrush? Themed(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnThemeChanged();
    }

    private void OnThemeChanged()
    {
        foreach (var presenter in _live.Concat(_staging))
        {
            presenter.Background = Paper;
        }

        InvalidateVisual();
        _overlay.InvalidateVisual();
    }

    public IReadOnlyList<object>? Pages
    {
        get => GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    public int PageIndex
    {
        get => GetValue(PageIndexProperty);
        set => SetValue(PageIndexProperty, value);
    }

    public int MaxPageIndex
    {
        get => GetValue(MaxPageIndexProperty);
        set => SetValue(MaxPageIndexProperty, value);
    }

    public bool AnimatesTurns
    {
        get => GetValue(AnimatesTurnsProperty);
        set => SetValue(AnimatesTurnsProperty, value);
    }

    /// <summary>True while two pages are shown side by side.</summary>
    public bool IsSpread => _isSpread;

    public static readonly DirectProperty<BookView, bool> IsSpreadProperty =
        AvaloniaProperty.RegisterDirect<BookView, bool>(nameof(IsSpread), o => o.IsSpread);

    /// <summary>True while a page is turning.</summary>
    public bool IsTurning => _turn is not null;

    private int PageCount => Pages?.Count ?? 0;

    // The last page (spread) the reader may turn to: the book's last, or the one holding MaxPageIndex.
    private int LastPosition => Math.Min(
        _isSpread ? SpreadLayout.LastSpread(PageCount) : Math.Max(0, PageCount - 1),
        PositionOf(MaxPageIndex));

    /// <summary>Turns one page (spread) forward.</summary>
    public void Next() => TurnTo((_queuedTarget ?? _turn?.Target ?? _shown) + 1);

    /// <summary>Turns one page (spread) back.</summary>
    public void Previous() => TurnTo((_queuedTarget ?? _turn?.Target ?? _shown) - 1);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PagesProperty)
        {
            CancelTurn();
            _shown = PositionOf(PageIndex);
            ShowIn(_live, _shown);
        }
        else if (change.Property == PageIndexProperty && !_writingPageIndex)
        {
            TurnTo(PositionOf(PageIndex));
        }
        else if (change.Property == BookmarkedPagesProperty)
        {
            _overlay.InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        var spread = UseSpread(e.NewSize);
        if (spread == _isSpread)
        {
            return;
        }

        // Switching between one and two pages: drop any turn and show the reading page in the new mode.
        CancelTurn();
        _isSpread = spread;
        RaisePropertyChanged(IsSpreadProperty, !spread, spread);
        _shown = PositionOf(PageIndex);
        ShowIn(_live, _shown);

        // This runs after the arrange pass that changed the size, which placed the pages for the old mode.
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 800 : availableSize.Height);

        var layout = BookLayout.For(size, UseSpread(size));
        foreach (var presenter in _live.Concat(_staging))
        {
            presenter.Measure(layout.RightPage.Size);
        }

        _overlay.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = BookLayout.For(finalSize, _isSpread);
        foreach (var pair in new[] { _live, _staging })
        {
            pair[0].Arrange(layout.LeftPage);
            pair[1].Arrange(layout.RightPage);
        }

        _overlay.Arrange(new Rect(finalSize));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        DrawTable(context, BookLayout.For(Bounds.Size, _isSpread), _shown);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right or Key.PageDown:
                Next();
                break;
            case Key.Left or Key.PageUp:
                Previous();
                break;
            case Key.Home:
                TurnTo(0);
                break;
            case Key.End:
                TurnTo(LastPosition);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---- Turning ----------------------------------------------------------------------------------------

    private void TurnTo(int target)
    {
        target = Math.Clamp(target, 0, LastPosition);

        if (_turn is not null)
        {
            _queuedTarget = target == _turn.Target ? null : target;
            return;
        }

        if (target == _shown)
        {
            WritePageIndex(_shown);
            return;
        }

        if (!AnimatesTurns || TopLevel.GetTopLevel(this) is null || Bounds.Width <= 0)
        {
            _shown = target;
            ShowIn(_live, _shown);
            WritePageIndex(_shown);
            return;
        }

        var turn = BeginTurn(target, bottomCorner: true);
        Animate(turn, turn.EndCorner(towardsLater: turn.Forward), towardsLater: turn.Forward);
    }

    private Turn BeginTurn(int target, bool bottomCorner)
    {
        // The target goes into the presenters underneath; one layout pass later both spreads can be captured.
        ShowIn(_staging, target);
        UpdateLayout();

        var from = Capture(_live);
        var to = Capture(_staging);
        var forward = target > _shown;
        var layout = BookLayout.For(Bounds.Size, _isSpread);

        var turn = new Turn(
            from: _shown,
            target: target,
            forward: forward,
            layout: layout,
            bottomCorner: bottomCorner,
            earlier: forward ? from : to,
            later: forward ? to : from);

        turn.Corner = turn.EndCorner(towardsLater: !forward);
        _turn = turn;
        _overlay.InvalidateVisual();
        return turn;
    }

    /// <summary>Moves the lifted corner to <paramref name="to"/>, then ends the turn on that side.</summary>
    private void Animate(Turn turn, Point to, bool towardsLater)
    {
        var from = turn.Corner;
        var lift = turn.Layout.RightPage.Height * 0.18 * (turn.BottomCorner ? -1 : 1);
        TimeSpan? start = null;

        RequestFrame();

        void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);

        void OnFrame(TimeSpan now)
        {
            if (_turn != turn)
            {
                return;
            }

            start ??= now;
            var t = Math.Clamp((now - start.Value).TotalMilliseconds / TurnDuration.TotalMilliseconds, 0, 1);
            var eased = t < 0.5 ? 2 * t * t : 1 - (Math.Pow((-2 * t) + 2, 2) / 2);

            var x = from.X + ((to.X - from.X) * eased);
            var y = from.Y + ((to.Y - from.Y) * eased) + (lift * Math.Sin(Math.PI * eased) * Math.Abs(to.X - from.X) / (2 * turn.Layout.RightPage.Width));
            turn.Corner = new Point(x, y);
            _overlay.InvalidateVisual();

            if (t >= 1)
            {
                EndTurn(towardsLater);
            }
            else
            {
                RequestFrame();
            }
        }
    }

    private void EndTurn(bool atLater)
    {
        if (_turn is not { } turn)
        {
            return;
        }

        var end = atLater == turn.Forward ? turn.Target : turn.From;
        if (end == turn.Target)
        {
            (_live, _staging) = (_staging, _live);
            SetLayers();
        }

        ShowIn(_staging, -1);
        turn.Dispose();
        _turn = null;
        _shown = end;
        WritePageIndex(end);
        _overlay.InvalidateVisual();
        InvalidateVisual();

        if (_queuedTarget is { } queued)
        {
            _queuedTarget = null;
            TurnTo(queued);
        }
    }

    private void CancelTurn()
    {
        _press = null;
        _queuedTarget = null;
        if (_turn is null)
        {
            return;
        }

        _turn.Dispose();
        _turn = null;
        ShowIn(_staging, -1);
        _overlay.InvalidateVisual();
    }

    // ---- Pointer ----------------------------------------------------------------------------------------

    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A button in the strip (a sentence's 听这一句, a character) keeps its click; the page turns from beside it.
        if (_turn is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsOnButton(e.Source))
        {
            return;
        }

        var point = e.GetPosition(this);
        var layout = BookLayout.For(Bounds.Size, _isSpread);
        var right = layout.RightPage;

        int? target = null;
        var canDrag = false;
        if (_shown < LastPosition && right.Contains(point) && point.X >= right.Right - GrabStripWidth)
        {
            target = _shown + 1;
            canDrag = true;
        }
        else if (_shown > 0 && _isSpread && layout.LeftPage.Contains(point) && point.X <= layout.LeftPage.Left + GrabStripWidth)
        {
            target = _shown - 1;
            canDrag = true;
        }
        else if (_shown > 0 && !_isSpread && right.Contains(point) && point.X <= right.Left + GrabStripWidth)
        {
            // One page: the earlier page lies off screen to the left; dragging rightwards brings it back over.
            target = _shown - 1;
            canDrag = true;
        }

        if (target is null)
        {
            return;
        }

        _press = new Press(target.Value, point, canDrag);
        _press.Track(point, e.Timestamp);
        e.Pointer.Capture(this);
        e.Handled = true;
        Focus();
    }

    private bool IsOnButton(object? source)
    {
        for (var visual = source as Visual; visual is not null && visual != this; visual = visual.GetVisualParent())
        {
            if (visual is Button { IsEffectivelyEnabled: true })
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_press is not { } press)
        {
            return;
        }

        var point = e.GetPosition(this);
        press.Track(point, e.Timestamp);
        if (!press.Dragging)
        {
            if (!press.CanDrag || ((Vector)(point - press.Start)).Length < DragThreshold)
            {
                return;
            }

            var layout = BookLayout.For(Bounds.Size, _isSpread);
            press.Dragging = true;
            press.StartCorner = BeginTurn(press.Target, bottomCorner: press.Start.Y > layout.RightPage.Center.Y).Corner;
        }

        if (_turn is { } turn)
        {
            // The corner moves as the finger does, from where it rests. On one page it has two page widths to cross
            // (onto the off-screen page) and the finger only one, so it goes twice as far.
            var moved = point - press.Start;
            var across = _isSpread ? 1 : 2;
            turn.Corner = press.StartCorner + new Vector(moved.X * across, moved.Y);
            _overlay.InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is not { } press)
        {
            return;
        }

        _press = null;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (!press.Dragging)
        {
            TurnTo(press.Target);
            return;
        }

        if (_turn is { } turn)
        {
            var finishes = FinishesTurn(turn, press, e.GetPosition(this), e.Timestamp);
            var towardsLater = finishes == turn.Forward;
            Animate(turn, turn.EndCorner(towardsLater), towardsLater);
        }
    }

    /// <summary>
    /// Whether a drag let go here turns the page or lets it fall back: dragged a quarter of the page's width towards the
    /// turn, flicked that way, or (on a spread) its corner carried past the spine. On a phone a full-width drag is
    /// awkward, so the distance and the flick count as much as the spine.
    /// </summary>
    private static bool FinishesTurn(Turn turn, Press press, Point point, ulong timestamp)
    {
        // Positive towards the turn: leftwards for a forward one, rightwards for a backward one.
        var sign = turn.Forward ? -1 : 1;
        var along = (point.X - press.Start.X) * sign;
        var speed = press.SpeedX(timestamp) * sign;
        var pastSpine = (turn.Geometry().Corner.X < turn.Layout.Spine) == turn.Forward;
        return along >= turn.Layout.RightPage.Width * TurnDistance || (speed >= FlickSpeed && along >= FlickDistance) || pastSpine;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_press is { Dragging: true } && _turn is { } turn)
        {
            _press = null;
            Animate(turn, turn.EndCorner(towardsLater: !turn.Forward), towardsLater: !turn.Forward);
        }

        _press = null;
    }

    // ---- Pages ------------------------------------------------------------------------------------------

    private ContentControl CreatePresenter() => new()
    {
        // Hidden until it holds a page: an empty presenter would still paint blank paper.
        IsVisible = false,
        Background = Paper,
        ClipToBounds = true,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
    };

    private void SetLayers()
    {
        foreach (var presenter in _live)
        {
            presenter.ZIndex = 1;
        }

        foreach (var presenter in _staging)
        {
            presenter.ZIndex = 0;
        }
    }

    /// <summary>Fills a pair of presenters with the pages at a position; -1 empties them.</summary>
    private void ShowIn(ContentControl[] pair, int position)
    {
        var (left, right) = position < 0 ? (-1, -1) : PagesAt(position);
        Fill(pair[0], left);
        Fill(pair[1], right);
        if (pair == _live) _overlay.InvalidateVisual();

        void Fill(ContentControl presenter, int page)
        {
            presenter.Content = page >= 0 ? Pages![page] : null;
            presenter.IsVisible = page >= 0;
        }
    }

    private (int Left, int Right) PagesAt(int position) =>
        _isSpread
            ? (SpreadLayout.LeftPage(position, PageCount), SpreadLayout.RightPage(position, PageCount))
            : (-1, position < PageCount ? position : -1);

    private int PositionOf(int pageIndex)
    {
        var page = Math.Clamp(pageIndex, 0, Math.Max(0, PageCount - 1));
        return _isSpread ? SpreadLayout.SpreadOf(page) : page;
    }

    private void WritePageIndex(int position)
    {
        var page = _isSpread ? SpreadLayout.ReadingPage(position, PageCount) : position;

        // The ribbons belong to the pages now on show.
        _overlay.InvalidateVisual();
        _writingPageIndex = true;
        try
        {
            SetCurrentValue(PageIndexProperty, page);
        }
        finally
        {
            _writingPageIndex = false;
        }
    }

    private Snapshot Capture(ContentControl[] pair) => new(CaptureOne(pair[0]), CaptureOne(pair[1]));

    private Bitmap? CaptureOne(ContentControl presenter)
    {
        if (!presenter.IsVisible || presenter.Bounds.Width < 1 || presenter.Bounds.Height < 1)
        {
            return null;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var pixels = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(presenter.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(presenter.Bounds.Height * scale)));
        var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(presenter);
        return bitmap;
    }

    private static bool UseSpread(Size size) => size.Width >= MinSpreadWidth && size.Width >= size.Height * 1.15;

    // ---- Drawing ----------------------------------------------------------------------------------------

    private void DrawTable(DrawingContext context, BookLayout layout, int position)
    {
        context.FillRectangle(Table, new Rect(Bounds.Size));
        if (PageCount == 0)
        {
            return;
        }

        var (left, right) = PagesAt(position);
        var book = left >= 0 && right >= 0 ? layout.LeftPage.Union(layout.RightPage)
            : left >= 0 ? layout.LeftPage
            : layout.RightPage;
        context.DrawRectangle(Paper, null, new RoundedRect(book, 3), BookShadow);

        // The page block's thickness: a line or two per side for the pages already read and still to come.
        if (right >= 0)
        {
            DrawPageEdges(context, layout.RightPage, PageCount - 1 - right, toRight: true);
        }

        if (left >= 0)
        {
            DrawPageEdges(context, layout.LeftPage, left, toRight: false);
        }
    }

    private void DrawPageEdges(DrawingContext context, Rect page, int pagesBeyond, bool toRight)
    {
        var lines = Math.Min(4, (pagesBeyond + 9) / 10);
        var pen = PageEdgePen;
        for (var i = 1; i <= lines; i++)
        {
            var x = toRight ? page.Right + (i * 1.5) : page.Left - (i * 1.5);
            context.DrawLine(pen, new Point(x, page.Top + i), new Point(x, page.Bottom - i));
        }
    }

    private void RenderOverlay(DrawingContext context)
    {
        var layout = BookLayout.For(Bounds.Size, _isSpread);
        if (_turn is not { } turn)
        {
            DrawSpine(context, layout);
            DrawRibbons(context, layout);
            return;
        }

        // The overlay covers both presenter pairs, so it repaints everything the turn shows.
        DrawTable(context, layout, Math.Min(turn.From, turn.Target));

        var geometry = turn.Geometry();
        var pageRect = layout.RightPage;

        if (_isSpread)
        {
            DrawPage(context, turn.Earlier.Left, layout.LeftPage);
        }

        using (context.PushGeometryClip(Polygon(geometry.FlatRegion())))
        {
            DrawPage(context, turn.Earlier.Right, pageRect);
        }

        DrawSpine(context, layout);

        if (!geometry.HasFold)
        {
            return;
        }

        var lifted = geometry.LiftedRegion();
        using (context.PushGeometryClip(Polygon(lifted)))
        {
            context.FillRectangle(Table, pageRect);
            DrawPage(context, turn.Later.Right, pageRect);
            DrawBand(context, geometry, geometry.FoldNormal, UncoveredShade, Math.Min(70, 8 + (((Vector)(geometry.RestingCorner - geometry.Corner)).Length * 0.25)));
        }

        using (context.PushTransform(geometry.BackPageTransform()))
        using (context.PushGeometryClip(Polygon(geometry.BackPageRegion())))
        {
            var back = PageCurlGeometry.MirrorAboutVertical(geometry.Spine).Transform(pageRect.TopRight);
            var backRect = new Rect(back, pageRect.Size);
            if (turn.Later.Left is { } backPage)
            {
                DrawPage(context, backPage, backRect);
            }
            else
            {
                context.FillRectangle(Paper, backRect);
            }
        }

        using (context.PushGeometryClip(Polygon(geometry.FlapRegion())))
        {
            DrawBand(context, geometry, -geometry.FoldNormal, FlapShade, Math.Min(90, 12 + (((Vector)(geometry.RestingCorner - geometry.Corner)).Length * 0.3)));
        }
    }

    /// <summary>The fold at the binding, drawn only when there is a left page to fold into.</summary>
    private void DrawSpine(DrawingContext context, BookLayout layout)
    {
        var position = _turn is null ? _shown : Math.Min(_turn.From, _turn.Target);
        if (!_isSpread || PagesAt(position).Left < 0)
        {
            return;
        }

        var spine = layout.Spine;
        context.FillRectangle(SpineShade, new Rect(spine - 28, layout.RightPage.Top, 56, layout.RightPage.Height));
    }

    /// <summary>A ribbon hanging over the top edge, near the outer corner, of each bookmarked page on show.</summary>
    private void DrawRibbons(DrawingContext context, BookLayout layout)
    {
        if (BookmarkedPages is not { Count: > 0 } marked)
        {
            return;
        }

        var brush = Themed("AccentBrush") ?? RibbonBrush;
        var (left, right) = PagesAt(_shown);
        foreach (var (page, rect, outerLeft) in new[] { (left, layout.LeftPage, true), (right, layout.RightPage, false) })
        {
            if (page < 0 || !marked.Contains(page))
            {
                continue;
            }

            const double width = 18, length = 46, notch = 7;
            var x = outerLeft ? rect.Left + 12 : rect.Right - 12 - width;
            var top = rect.Top - 4;
            var ribbon = new StreamGeometry();
            using (var ctx = ribbon.Open())
            {
                ctx.BeginFigure(new Point(x, top), true);
                ctx.LineTo(new Point(x + width, top));
                ctx.LineTo(new Point(x + width, top + length));
                ctx.LineTo(new Point(x + (width / 2), top + length - notch));
                ctx.LineTo(new Point(x, top + length));
                ctx.EndFigure(true);
            }

            context.FillRectangle(RibbonShade, new Rect(x - 1, top, width + 2, 4));
            context.DrawGeometry(brush, null, ribbon);
        }
    }

    private static void DrawPage(DrawingContext context, Bitmap? page, Rect destination)
    {
        if (page is not null)
        {
            context.DrawImage(page, new Rect(0, 0, page.PixelSize.Width, page.PixelSize.Height), destination);
        }
    }

    /// <summary>A shading band along the fold, <paramref name="width"/> wide, on the <paramref name="side"/> side.</summary>
    private static void DrawBand(DrawingContext context, PageCurlGeometry geometry, Vector side, IBrush brush, double width)
    {
        var reach = geometry.Page.Width + geometry.Page.Height;
        using (context.PushTransform(geometry.FoldFrame(side)))
        {
            context.FillRectangle(brush, new Rect(0, -reach, width, 2 * reach));
        }
    }

    private static StreamGeometry Polygon(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        if (points.Count > 0)
        {
            ctx.BeginFigure(points[0], true);
            for (var i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i]);
            }

            ctx.EndFigure(true);
        }

        return geometry;
    }

    private static IBrush HorizontalBand(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        return brush.ToImmutable();
    }

    // ---- Nested types -----------------------------------------------------------------------------------

    /// <summary>Where the pages sit inside the control.</summary>
    public readonly record struct BookLayout(Rect LeftPage, Rect RightPage)
    {
        public double Spine => RightPage.Left;

        public static BookLayout For(Size size, bool spread)
        {
            var marginX = spread ? 32 : 10;
            var marginY = spread ? 24 : 10;
            var height = Math.Max(1, size.Height - (2 * marginY));

            if (spread)
            {
                var pageWidth = Math.Max(1, (size.Width - (2 * marginX)) / 2);
                var spine = marginX + pageWidth;
                return new BookLayout(
                    new Rect(marginX, marginY, pageWidth, height),
                    new Rect(spine, marginY, pageWidth, height));
            }

            // One page: the "left page" is the off-screen spot a turned sheet falls to.
            var width = Math.Max(1, size.Width - (2 * marginX));
            return new BookLayout(
                new Rect(marginX - width, marginY, width, height),
                new Rect(marginX, marginY, width, height));
        }
    }

    private sealed record Snapshot(Bitmap? Left, Bitmap? Right) : IDisposable
    {
        public void Dispose()
        {
            Left?.Dispose();
            Right?.Dispose();
        }
    }

    /// <summary>
    /// One page turn, always modelled as the earlier spread's right page turning onto the later spread; a
    /// backward turn runs it from the left end.
    /// </summary>
    private sealed class Turn(int from, int target, bool forward, BookLayout layout, bool bottomCorner, Snapshot earlier, Snapshot later)
        : IDisposable
    {
        public int From { get; } = from;

        public int Target { get; } = target;

        public bool Forward { get; } = forward;

        public BookLayout Layout { get; } = layout;

        public bool BottomCorner { get; } = bottomCorner;

        public Snapshot Earlier { get; } = earlier;

        public Snapshot Later { get; } = later;

        /// <summary>Current position of the lifted corner (unconstrained; the geometry keeps it on the paper).</summary>
        public Point Corner { get; set; }

        public PageCurlGeometry Geometry() => PageCurlGeometry.Create(Layout.RightPage, BottomCorner, Corner);

        /// <summary>Where the corner rests at either end: on the right page (earlier spread) or mirrored onto the left.</summary>
        public Point EndCorner(bool towardsLater)
        {
            var page = Layout.RightPage;
            var y = BottomCorner ? page.Bottom : page.Top;
            return towardsLater ? new Point(page.Left - page.Width, y) : new Point(page.Right, y);
        }

        public void Dispose()
        {
            Earlier.Dispose();
            Later.Dispose();
        }
    }

    private sealed class Press(int target, Point start, bool canDrag)
    {
        /// <summary>How far back the speed of a flick is measured.</summary>
        private const ulong SpeedWindow = 100;

        private readonly Queue<(Point Point, ulong Time)> _recent = new();

        public int Target { get; } = target;

        public Point Start { get; } = start;

        public bool CanDrag { get; } = canDrag;

        public bool Dragging { get; set; }

        /// <summary>Where the turn's corner rested when the drag began.</summary>
        public Point StartCorner { get; set; }

        public void Track(Point point, ulong time)
        {
            _recent.Enqueue((point, time));
            while (_recent.Count > 2 && time - _recent.Peek().Time > SpeedWindow)
            {
                _recent.Dequeue();
            }
        }

        /// <summary>Horizontal speed over the last moments of the drag, pixels per millisecond (rightwards positive).</summary>
        public double SpeedX(ulong now)
        {
            if (_recent.Count < 2)
            {
                return 0;
            }

            // No time between the samples (events without timestamps) is no speed at all, not an infinite one.
            var first = _recent.Peek();
            var last = _recent.Last();
            var elapsed = (double)(Math.Max(now, last.Time) - first.Time);
            return elapsed <= 0 ? 0 : (last.Point.X - first.Point.X) / elapsed;
        }
    }

    private sealed class TurnOverlay(BookView owner) : Control
    {
        public override void Render(DrawingContext context) => owner.RenderOverlay(context);
    }
}
