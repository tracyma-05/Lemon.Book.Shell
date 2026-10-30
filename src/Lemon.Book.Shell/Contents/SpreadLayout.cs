namespace Lemon.Book.Shell.Contents;

/// <summary>
/// Maps page indices to two-page spreads. Like a printed book, the cover (page 0) sits alone on the right;
/// after that spread <c>s</c> shows pages <c>2s - 1</c> (left) and <c>2s</c> (right).
/// </summary>
public static class SpreadLayout
{
    public static int SpreadOf(int pageIndex) => (Math.Max(0, pageIndex) + 1) / 2;

    public static int LastSpread(int pageCount) => pageCount <= 0 ? 0 : SpreadOf(pageCount - 1);

    /// <summary>Left page of a spread, or -1 when the spread has none (the cover).</summary>
    public static int LeftPage(int spread, int pageCount) => Existing((2 * spread) - 1, pageCount);

    /// <summary>Right page of a spread, or -1 past the last page.</summary>
    public static int RightPage(int spread, int pageCount) => Existing(2 * spread, pageCount);

    /// <summary>The page that stands for a spread when the reader's place is saved: its left page, else its right.</summary>
    public static int ReadingPage(int spread, int pageCount)
    {
        var left = LeftPage(spread, pageCount);
        return left >= 0 ? left : Math.Max(0, RightPage(spread, pageCount));
    }

    private static int Existing(int index, int pageCount) => index >= 0 && index < pageCount ? index : -1;
}
