using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lemon.Book.Shell.Contents;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.ViewModels.Pages;

namespace Lemon.Book.Shell.ViewModels;

/// <summary>
/// The bookmarks button: mark the reading page, see the marked pages and turn to one. The book draws a ribbon on
/// each marked page (<see cref="MarkedPages"/>).
/// </summary>
public sealed partial class BookmarksViewModel : ObservableObject
{
    private readonly Bookmarks _bookmarks;
    private readonly IReadOnlyList<PageViewModel> _pages;
    private readonly IBookNavigator _navigator;
    private int _current;

    public BookmarksViewModel(Bookmarks bookmarks, IReadOnlyList<PageViewModel> pages, IBookNavigator navigator, int current)
    {
        _bookmarks = bookmarks;
        _pages = pages;
        _navigator = navigator;
        _current = current;
        bookmarks.Changed += (_, _) => Refresh();
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        Refresh();
    }

    /// <summary>Newest first.</summary>
    [ObservableProperty]
    private IReadOnlyList<BookmarkEntry> _items = [];

    /// <summary>The marked page numbers, for the ribbons the book draws.</summary>
    [ObservableProperty]
    private IReadOnlyCollection<int> _markedPages = [];

    public bool HasItems => Items.Count > 0;

    public bool IsCurrentMarked => _bookmarks.Contains(_current);

    /// <summary>"Bookmark this page" or "Remove this page's bookmark".</summary>
    public string ToggleText => LocalizationService.Instance.GetString(IsCurrentMarked ? "Bookmarks_Remove" : "Bookmarks_Add");

    /// <summary>What the reading page is, e.g. "Chapter 5 · Day and night".</summary>
    public string CurrentCaption => _pages[_current].Caption;

    /// <summary>The reader turned to another page.</summary>
    public void SetCurrent(int page)
    {
        _current = Math.Clamp(page, 0, _pages.Count - 1);
        RaiseCurrent();
    }

    [RelayCommand]
    private void ToggleCurrent() => _bookmarks.Toggle(_current);

    private void Refresh()
    {
        Items = _bookmarks.Items
            .Select(b => new BookmarkEntry(_pages[b.Page], b.Added, _navigator, _bookmarks))
            .ToList();
        MarkedPages = _bookmarks.Items.Select(b => b.Page).ToHashSet();
        OnPropertyChanged(nameof(HasItems));
        RaiseCurrent();
    }

    private void RaiseCurrent()
    {
        OnPropertyChanged(nameof(IsCurrentMarked));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(CurrentCaption));
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var item in Items) item.RefreshText();
        RaiseCurrent();
    }
}

/// <summary>One bookmark in the list: what the page is, its number and when it was marked.</summary>
public sealed partial class BookmarkEntry(PageViewModel page, DateTime added, IBookNavigator navigator, Bookmarks bookmarks)
    : ObservableObject
{
    public int PageIndex => page.Index;

    public string Caption => page.Caption;

    /// <summary>e.g. "Page 15 · 22 Oct".</summary>
    public string Details => LocalizationService.Instance.Format(
        "Bookmarks_Details", page.Index, added.ToLocalTime().ToString("M", LocalizationService.Instance.CurrentCulture));

    internal void RefreshText()
    {
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(Details));
    }

    [RelayCommand]
    private void Open() => navigator.GoToPage(page.Index);

    [RelayCommand]
    private void Remove() => bookmarks.Remove(page.Index);
}
