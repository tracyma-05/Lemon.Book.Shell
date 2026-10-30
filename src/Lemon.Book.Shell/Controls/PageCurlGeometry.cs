using Avalonia;

namespace Lemon.Book.Shell.Controls;

/// <summary>
/// The flat-paper model of a page turn, kept free of rendering so it can be tested.
/// </summary>
/// <remarks>
/// The turning sheet is the right-hand page <c>[spine, spine + width]</c>. Its outer corner (top or bottom) has
/// been lifted to <see cref="Corner"/>. The fold is the perpendicular bisector between where the corner rested
/// and where it is now: the part of the page on the corner's side is lifted, uncovering the page beneath, and its
/// mirror image across the fold is the flap that shows the back of the sheet. A backward turn is the same
/// geometry run in reverse, so there is no left-hand variant.
/// </remarks>
internal readonly struct PageCurlGeometry
{
    private PageCurlGeometry(Rect page, Point restingCorner, Point corner, Point foldOrigin, Vector foldNormal)
    {
        Page = page;
        RestingCorner = restingCorner;
        Corner = corner;
        FoldOrigin = foldOrigin;
        FoldNormal = foldNormal;
    }

    /// <summary>The right-hand page being turned.</summary>
    public Rect Page { get; }

    public double Spine => Page.Left;

    /// <summary>Where the lifted corner sits on the unturned page.</summary>
    public Point RestingCorner { get; }

    /// <summary>Where the lifted corner is now.</summary>
    public Point Corner { get; }

    /// <summary>A point on the fold line: the midpoint between resting and current corner.</summary>
    public Point FoldOrigin { get; }

    /// <summary>Unit normal of the fold, pointing to the lifted side (towards the resting corner).</summary>
    public Vector FoldNormal { get; }

    /// <summary>True once the corner has moved far enough for a fold to exist.</summary>
    public bool HasFold => FoldNormal != default;

    /// <summary>How far the turn has gone: 0 with the page flat, 1 with it lying on the left.</summary>
    public double Progress => Math.Clamp((RestingCorner.X - Corner.X) / (2 * Page.Width), 0, 1);

    /// <summary>
    /// Builds the geometry for the corner at <paramref name="corner"/>, after keeping it where real paper can
    /// reach (see <see cref="Constrain"/>).
    /// </summary>
    public static PageCurlGeometry Create(Rect page, bool bottomCorner, Point corner)
    {
        var restingCorner = bottomCorner ? page.BottomRight : page.TopRight;
        var constrained = Constrain(page, bottomCorner, corner);
        Vector delta = restingCorner - constrained;
        var length = delta.Length;
        if (length < 0.5)
        {
            return new PageCurlGeometry(page, restingCorner, constrained, restingCorner, default);
        }

        var origin = new Point((restingCorner.X + constrained.X) / 2, (restingCorner.Y + constrained.Y) / 2);
        return new PageCurlGeometry(page, restingCorner, constrained, origin, delta / length);
    }

    /// <summary>
    /// Keeps the corner no farther from the spine corner on the same edge than the page width, and no farther
    /// from the opposite spine corner than the page diagonal: the sheet is fixed at the spine and cannot stretch.
    /// </summary>
    public static Point Constrain(Rect page, bool bottomCorner, Point corner)
    {
        var sameEdgeSpine = bottomCorner ? page.BottomLeft : page.TopLeft;
        var otherEdgeSpine = bottomCorner ? page.TopLeft : page.BottomLeft;
        var diagonal = Math.Sqrt((page.Width * page.Width) + (page.Height * page.Height));

        // Two passes settle the rare case where pulling inside one circle pushes the point out of the other.
        for (var pass = 0; pass < 2; pass++)
        {
            corner = ClampToCircle(corner, sameEdgeSpine, page.Width);
            corner = ClampToCircle(corner, otherEdgeSpine, diagonal);
        }

        return corner;
    }

    /// <summary>The part of the turning page still lying flat (front side visible).</summary>
    public IReadOnlyList<Point> FlatRegion() =>
        HasFold ? ClipToHalfPlane(Corners(Page), FoldOrigin, -FoldNormal) : Corners(Page);

    /// <summary>The part of the turning page lifted off, which uncovers the page beneath.</summary>
    public IReadOnlyList<Point> LiftedRegion() =>
        HasFold ? ClipToHalfPlane(Corners(Page), FoldOrigin, FoldNormal) : [];

    /// <summary>The flap: the lifted region folded over, in book coordinates.</summary>
    public IReadOnlyList<Point> FlapRegion()
    {
        var reflection = FoldReflection();
        return LiftedRegion().Select(p => reflection.Transform(p)).ToArray();
    }

    /// <summary>
    /// The lifted region as it sits on the page that forms the back of the sheet (the next left-hand page),
    /// which is the turning page mirrored about the spine.
    /// </summary>
    public IReadOnlyList<Point> BackPageRegion()
    {
        var mirror = MirrorAboutVertical(Spine);
        return LiftedRegion().Select(p => mirror.Transform(p)).ToArray();
    }

    /// <summary>
    /// Maps the back page, drawn at its own left-hand position, onto the flap: mirror about the spine (the back
    /// of the sheet), then reflect across the fold.
    /// </summary>
    public Matrix BackPageTransform() => MirrorAboutVertical(Spine) * FoldReflection();

    /// <summary>Reflection across the fold line.</summary>
    public Matrix FoldReflection()
    {
        if (!HasFold)
        {
            return Matrix.Identity;
        }

        var (nx, ny) = (FoldNormal.X, FoldNormal.Y);
        var d = (FoldOrigin.X * nx) + (FoldOrigin.Y * ny);
        return new Matrix(
            1 - (2 * nx * nx), -2 * nx * ny,
            -2 * nx * ny, 1 - (2 * ny * ny),
            2 * d * nx, 2 * d * ny);
    }

    /// <summary>
    /// A frame whose x axis runs from the fold along <paramref name="direction"/> and whose y axis runs along the
    /// fold, for drawing shading bands parallel to it.
    /// </summary>
    public Matrix FoldFrame(Vector direction) =>
        new(direction.X, direction.Y, -direction.Y, direction.X, FoldOrigin.X, FoldOrigin.Y);

    internal static Matrix MirrorAboutVertical(double x) => new(-1, 0, 0, 1, 2 * x, 0);

    internal static IReadOnlyList<Point> Corners(Rect rect) =>
        [rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft];

    /// <summary>
    /// Sutherland-Hodgman clip of a convex polygon against the half-plane <c>(p - origin) . normal &gt;= 0</c>.
    /// </summary>
    internal static IReadOnlyList<Point> ClipToHalfPlane(IReadOnlyList<Point> polygon, Point origin, Vector normal)
    {
        var result = new List<Point>(polygon.Count + 1);
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            var currentSide = Side(current);
            var nextSide = Side(next);

            if (currentSide >= 0)
            {
                result.Add(current);
            }

            if ((currentSide >= 0) != (nextSide >= 0))
            {
                var t = currentSide / (currentSide - nextSide);
                result.Add(current + ((next - current) * t));
            }
        }

        return result;

        double Side(Point p) => ((p.X - origin.X) * normal.X) + ((p.Y - origin.Y) * normal.Y);
    }

    internal static double Area(IReadOnlyList<Point> polygon)
    {
        double sum = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) / 2;
    }

    private static Point ClampToCircle(Point point, Point centre, double radius)
    {
        Vector offset = point - centre;
        var length = offset.Length;
        return length <= radius || length == 0 ? point : centre + (offset * (radius / length));
    }
}
