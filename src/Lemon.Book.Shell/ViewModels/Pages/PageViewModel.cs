using System.ComponentModel;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using Lemon.Book.Shell.Infrastructures.Localization;

namespace Lemon.Book.Shell.ViewModels.Pages;

/// <summary>Lets a page send the reader somewhere else in the book (contents links, the cover's open button).</summary>
public interface IBookNavigator
{
    void GoToPage(int pageIndex);
}

/// <summary>
/// One page of the book. Each concrete type has a matching view in <c>Views/Pages</c>, picked by the data
/// templates in <c>MainView</c>.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(int index, IBookNavigator navigator)
    {
        Index = index;
        Navigator = navigator;
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    public int Index { get; }

    protected IBookNavigator Navigator { get; }

    /// <summary>False for pages that carry no printed number (covers, title page).</summary>
    public virtual bool HasFolio => true;

    /// <summary>The printed page number, or empty.</summary>
    public string Folio => HasFolio ? Index.ToString(LocalizationService.Instance.CurrentCulture) : string.Empty;

    /// <summary>Odd pages are left-hand pages, so their number sits at the outer (left) corner.</summary>
    public HorizontalAlignment FolioAlignment => Index % 2 == 1 ? HorizontalAlignment.Left : HorizontalAlignment.Right;

    /// <summary>What the page is, for the bookmark list: a chapter's title, "Contents · Sky", or "Page 12".</summary>
    public virtual string Caption => LocalizationService.Instance.Format("Page_Caption", Index);

    /// <summary>Called when the reader switches language, to raise properties built from localized text.</summary>
    protected virtual void OnCultureChanged()
    {
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationService.CurrentCulture))
        {
            OnPropertyChanged(nameof(Folio));
            OnPropertyChanged(nameof(Caption));
            OnCultureChanged();
        }
    }
}
