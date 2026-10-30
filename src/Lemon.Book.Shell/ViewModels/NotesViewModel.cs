using System.ComponentModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lemon.Book.Shell.Contents;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.ViewModels.Pages;

namespace Lemon.Book.Shell.ViewModels;

/// <summary>
/// The notes button: write a note on the reading page (optionally quoting text selected on it), see and edit every
/// note, and export them as Markdown.
/// </summary>
public sealed partial class NotesViewModel : ObservableObject
{
    private readonly Notes _notes;
    private readonly IReadOnlyList<PageViewModel> _pages;
    private readonly IBookNavigator _navigator;
    private int _current;

    public NotesViewModel(Notes notes, IReadOnlyList<PageViewModel> pages, IBookNavigator navigator, int current)
    {
        _notes = notes;
        _pages = pages;
        _navigator = navigator;
        _current = current;
        notes.Changed += (_, _) => Refresh();
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        Refresh();
    }

    /// <summary>Asks the view to open the notes panel (after "add to notes" on selected text).</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The new note being written for the reading page.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _draft = string.Empty;

    /// <summary>Text quoted from the page for the new note, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraftQuote))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string? _draftQuote;

    /// <summary>Only the reading page's notes, or all of them.</summary>
    [ObservableProperty]
    private bool _showsCurrentPageOnly;

    [ObservableProperty]
    private IReadOnlyList<NoteEntry> _items = [];

    /// <summary>A line about the last export or copy, or empty.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    public bool HasDraftQuote => DraftQuote is not null;

    public bool HasNotes => _notes.Items.Count > 0;

    public bool HasItems => Items.Count > 0;

    /// <summary>e.g. "Chapter 6 · Tides".</summary>
    public string CurrentCaption => _pages[_current].Caption;

    public string CountText => LocalizationService.Instance.Format("Notes_Count", _notes.Items.Count);

    /// <summary>The reader turned to another page.</summary>
    public void SetCurrent(int page)
    {
        _current = Math.Clamp(page, 0, _pages.Count - 1);
        OnPropertyChanged(nameof(CurrentCaption));
        if (ShowsCurrentPageOnly) Refresh();
    }

    /// <summary>Starts a note quoting <paramref name="quote"/> (selected on the reading page) and opens the panel.</summary>
    public void AddQuote(string quote)
    {
        if (string.IsNullOrWhiteSpace(quote)) return;
        DraftQuote = quote.Trim();
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        _notes.Add(_current, DraftQuote, Draft);
        Draft = string.Empty;
        DraftQuote = null;
        Status = string.Empty;
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(Draft) || DraftQuote is not null;

    [RelayCommand]
    private void ClearQuote() => DraftQuote = null;

    partial void OnShowsCurrentPageOnlyChanged(bool value) => Refresh();

    /// <summary>
    /// All notes as Markdown, in book order: a heading per page (what it is and its number), each note's quote as a
    /// block quote, then its text and when it was written.
    /// </summary>
    public string ToMarkdown()
    {
        var localization = LocalizationService.Instance;
        var culture = localization.CurrentCulture;
        var text = new StringBuilder();
        text.Append("# ").AppendLine(localization.GetString("Notes_Export_Title"));
        text.AppendLine();
        text.AppendLine(localization.Format("Notes_Export_Summary", DateTime.Now.ToString("g", culture), _notes.Items.Count));

        foreach (var page in _notes.Items.GroupBy(n => n.Page).OrderBy(g => g.Key))
        {
            text.AppendLine();
            text.Append("## ").AppendLine(localization.Format("Notes_Export_Page", _pages[page.Key].Caption, page.Key));
            foreach (var note in page.OrderBy(n => n.Created))
            {
                text.AppendLine();
                if (note.Quote is { } quote)
                {
                    foreach (var line in quote.ReplaceLineEndings("\n").Split('\n'))
                    {
                        text.Append("> ").AppendLine(line);
                    }

                    text.AppendLine();
                }

                if (note.Text.Length > 0)
                {
                    text.AppendLine(note.Text);
                    text.AppendLine();
                }

                text.Append('*').Append(note.Created.ToLocalTime().ToString("g", culture)).AppendLine("*");
            }
        }

        return text.ToString();
    }

    /// <summary>A file name for the export, e.g. "Explore-Book-notes-2026-10-22.md".</summary>
    public string ExportFileName => LocalizationService.Instance.Format(
        "Notes_Export_FileName", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    internal void ReportExported(string where) => Status = LocalizationService.Instance.Format("Notes_Exported", where);

    internal void ReportCopied() => Status = LocalizationService.Instance.GetString("Notes_Copied");

    private void Refresh()
    {
        Items = _notes.Items
            .Where(n => !ShowsCurrentPageOnly || n.Page == _current)
            .Select(n => new NoteEntry(n, _pages[n.Page], _navigator, _notes))
            .ToList();
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountText));
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var item in Items) item.RefreshText();
        OnPropertyChanged(nameof(CurrentCaption));
        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>One note in the list: its page, quote and text, editable in place.</summary>
public sealed partial class NoteEntry(NoteRecord note, PageViewModel page, IBookNavigator navigator, Notes notes) : ObservableObject
{
    public string Caption => page.Caption;

    public string? Quote => note.Quote;

    public bool HasQuote => note.Quote is not null;

    public string Text => note.Text;

    public bool HasText => note.Text.Length > 0;

    /// <summary>e.g. "Page 17 · 22 Oct 14:05".</summary>
    public string Details => LocalizationService.Instance.Format(
        "Notes_Details", page.Index, note.Updated.ToLocalTime().ToString("g", LocalizationService.Instance.CurrentCulture));

    /// <summary>The note's text, while it is not being edited.</summary>
    public bool ShowsText => HasText && !IsEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsText))]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    internal void RefreshText()
    {
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(Details));
    }

    [RelayCommand]
    private void Open() => navigator.GoToPage(page.Index);

    [RelayCommand]
    private void Edit()
    {
        EditText = note.Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void Save()
    {
        IsEditing = false;
        notes.Update(note.Id, EditText);
    }

    [RelayCommand]
    private void Cancel() => IsEditing = false;

    [RelayCommand]
    private void Remove() => notes.Remove(note.Id);
}
