# Lemon.Book.Shell

The shell the Lemon books share (Avalonia 12, .NET 10): an open book with page turning, narration with timed
subtitles, lesson walkthroughs, bookmarks and notes, settings, localization, the light, dark and eye-care (护眼)
themes, updates from GitHub releases, and optional sign-in with sync. A book brings its own pages; the shell brings
everything around them.

| Package | What is in it |
|---|---|
| `Lemon.Book.Shell` | `BookView` and the page turn, `PageViewModel`, narration and lessons, bookmarks, notes, reading progress, settings stores, `LocalizationService`, `BookTheme`, updates, sync, and the shared look (`Styles/Book.axaml`) |
| `Lemon.Book.Shell.Desktop` | Narration players for Windows (Media Foundation, MCI) and macOS, and signing in through the system browser |
| `Lemon.Book.Shell.Browser` | Narration through Web Audio, settings in `localStorage`, sign-in by redirect, and the page's scripts (`download-progress.js`, `wake-lock.js`, `narration.js`) |

## Using it in a book

At start, before anything reads the settings (the book's `App`, and its tests):

```csharp
BookShell.Configure(new BookShellOptions(
    AppAssembly: typeof(App).Assembly,      // build metadata, strings, narration manifest
    SyncApp: "my-book",                     // the book's name on the sync server
    PageCount: () => BookContents.PageCount,
    SyncRules: new MyBookSyncRules()));     // how the book's own progress merges, and its events
```

- **Strings**: the book's `Resources/AppStrings.resx` (embedded as `<assembly>.Resources.AppStrings.resources`) comes
  first, then the shell's `ShellStrings.resx`, so a book can reword any shell string. The book defines the lesson
  player's narration lines (`Narr_Common_Right`, `_Wrong`, `_Nice`, `_Reveal`, `_MoveOn`): each book renders its own
  recordings.
- **Narration**: the book embeds its manifest as `<assembly>.Narration.Manifest`.
- **Build metadata**: `<AssemblyMetadata Include="UpdateFeed" …>` and `SyncServer` in the book's project.
- **Look**: in `App.axaml`, after `<FluentTheme />`:
  `<StyleInclude Source="avares://Lemon.Book.Shell/Styles/Book.axaml" />`. The book's `Application.Resources`
  (its cover, its accent) come before the shell's.
- **Web**: `main.js` imports `./_content/Lemon.Book.Shell.Browser/download-progress.js` and `wake-lock.js`;
  `Program.cs` imports `WebNarrationAudio.ModuleName` from `WebNarrationAudio.ModuleUrl`; `sw.js` lists the three
  scripts in its shell.

## Changing the shell

```bash
dotnet test
scripts/pack.sh ../Lemon.English.Book
```

Bump `<Version>` in `Directory.Build.props` for every change the books should get. The books still to move onto the
package (the Picture, Maths and Explore books) carry their own copy of the shell until they do.

## Licence

MIT.
