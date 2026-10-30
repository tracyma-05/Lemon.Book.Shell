using Avalonia;
using Lemon.Book.Shell.Controls;
using Xunit;

namespace Lemon.Book.Shell.Tests;

public class PageCurlGeometryTests
{
    private static readonly Rect Page = new(500, 20, 400, 600);

    [Fact]
    public void Untouched_Page_HasNoFold()
    {
        var geometry = PageCurlGeometry.Create(Page, bottomCorner: true, Page.BottomRight);

        Assert.False(geometry.HasFold);
        Assert.Equal(0, geometry.Progress);
        Assert.Empty(geometry.LiftedRegion());
    }

    [Theory]
    [InlineData(820, 560)]
    [InlineData(700, 420)]
    [InlineData(510, 600)]
    [InlineData(300, 500)]
    public void Flat_And_Lifted_Parts_AddUpToThePage(double x, double y)
    {
        var geometry = PageCurlGeometry.Create(Page, bottomCorner: true, new Point(x, y));

        Assert.True(geometry.HasFold);
        var total = PageCurlGeometry.Area(geometry.FlatRegion()) + PageCurlGeometry.Area(geometry.LiftedRegion());
        Assert.Equal(Page.Width * Page.Height, total, 6);

        // The flap is a mirror image: same area as what was lifted.
        Assert.Equal(PageCurlGeometry.Area(geometry.LiftedRegion()), PageCurlGeometry.Area(geometry.FlapRegion()), 6);
    }

    [Fact]
    public void FullyTurned_Sheet_LiesOnTheLeftPage_WithTheBackPageUnchanged()
    {
        var turnedCorner = new Point(Page.Left - Page.Width, Page.Bottom);
        var geometry = PageCurlGeometry.Create(Page, bottomCorner: true, turnedCorner);

        Assert.Equal(1, geometry.Progress, 6);
        Assert.Equal(Page.Width * Page.Height, PageCurlGeometry.Area(geometry.LiftedRegion()), 6);

        // Mirror about the spine then reflect across a fold that is the spine: the back page is drawn as is.
        var transform = geometry.BackPageTransform();
        var leftPageCorner = new Point(Page.Left - Page.Width, Page.Top);
        AssertClose(leftPageCorner, transform.Transform(leftPageCorner));
    }

    [Fact]
    public void Reflection_MovesTheRestingCorner_ToTheLiftedCorner()
    {
        var geometry = PageCurlGeometry.Create(Page, bottomCorner: false, new Point(760, 140));

        AssertClose(geometry.Corner, geometry.FoldReflection().Transform(geometry.RestingCorner));
    }

    [Fact]
    public void Corner_StaysWithinReachOfTheSpine()
    {
        // Dragged far beyond what a sheet fixed at the spine can reach.
        var geometry = PageCurlGeometry.Create(Page, bottomCorner: true, new Point(-2000, 2000));

        Assert.True(Distance(geometry.Corner, Page.BottomLeft) <= Page.Width + 1e-6);
        var diagonal = Math.Sqrt((Page.Width * Page.Width) + (Page.Height * Page.Height));
        Assert.True(Distance(geometry.Corner, Page.TopLeft) <= diagonal + 1e-6);
    }

    private static double Distance(Point a, Point b) => ((Vector)(a - b)).Length;

    private static void AssertClose(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }
}
