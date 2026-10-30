using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.ViewModels;

namespace Lemon.Book.Shell.Views;

/// <summary>The notes panel; its data context is a <see cref="NotesViewModel"/>.</summary>
public partial class NotesView : UserControl
{
    public NotesView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the cursor in the new note, so the reader can start typing (after "add to notes").</summary>
    public void FocusDraft() => DraftBox.Focus();

    /// <summary>
    /// Saves the notes as a Markdown file the reader picks. Where files cannot be saved (some browsers), the same text
    /// goes on the clipboard instead.
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NotesViewModel notes || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        if (!top.StorageProvider.CanSave)
        {
            await CopyAsync(top, notes);
            return;
        }

        var localization = LocalizationService.Instance;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = localization.GetString("Notes_Export"),
            SuggestedFileName = notes.ExportFileName,
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new FilePickerFileType(localization.GetString("Notes_Export_Markdown")) { Patterns = ["*.md"], MimeTypes = ["text/markdown"] },
                new FilePickerFileType(localization.GetString("Notes_Export_Text")) { Patterns = ["*.txt"], MimeTypes = ["text/plain"] },
            ],
        });
        if (file is null)
        {
            return;
        }

        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(notes.ToMarkdown());
        }

        notes.ReportExported(file.TryGetLocalPath() ?? file.Name);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NotesViewModel notes && TopLevel.GetTopLevel(this) is { } top)
        {
            await CopyAsync(top, notes);
        }
    }

    private static async Task CopyAsync(TopLevel top, NotesViewModel notes)
    {
        if (top.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(notes.ToMarkdown());
            notes.ReportCopied();
        }
    }
}
