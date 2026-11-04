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

Signing in itself is in the hub client, three packages of its own with no Avalonia (`src/Lemon.Hub.Client`,
`.Desktop`, `.Browser`; see [its README](src/Lemon.Hub.Client/README.md)), which the shell depends on and apps that
are not books use directly. Since 0.2.0 the sign-in types live there (`Lemon.Hub.Client`: `ISignInFlow`,
`OidcClient`, `AccountSession`, `SignInException`; `Lemon.Hub.Client.Browser.RedirectSignInFlow`). The shell's packages
add those namespaces as global usings to the book (`buildTransitive`, with `ImplicitUsings`), so a book's code compiles
unchanged; `Lemon.Book.Shell.Desktop.LoopbackSignInFlow` stays, with the books' page. A book keeps the session
somewhere else by passing an `ISessionStore` to `SyncService` (by default it stays in the book's settings, as before).

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
  first, then the shell's `ShellStrings.resx` (Chinese, with `ShellStrings.en.resx` in English), so a book can reword
  any shell string. A book in both languages lists them in `BookShellOptions.Cultures` (`["en", "zh-CN"]`, the
  default first); the shell's strings never name a book, so a book words its name into those that should. The book defines the lesson
  player's narration lines (`Narr_Common_Right`, `_Wrong`, `_Nice`, `_Reveal`, `_MoveOn`): each book renders its own
  recordings.
- **Narration**: the book embeds its manifest as `<assembly>.Narration.Manifest`.
- **Build metadata**: `<AssemblyMetadata Include="UpdateFeed" …>` and `SyncServer` in the book's project.
- **Settings**: each head gives the book its own store, and a book keeps where it saved before it took the shell, so a
  reader's progress survives the move: `new JsonFileSettingsStore(JsonFileSettingsStore.DefaultPath("Lemon.My.Book"))`
  on the desktop (the folder under the application-data folder), `new LocalStorageSettingsStore("my-book:")` on the
  web (the `localStorage` prefix). The forms without one keep the first book's (`Lemon.Book.Shell`, `picture-book:`).
- **Look**: in `App.axaml`, after `<FluentTheme />`:
  `<StyleInclude Source="avares://Lemon.Book.Shell/Styles/Book.axaml" />`. The book's `Application.Resources`
  (its cover, its accent) come before the shell's.
- **Time**: walkthroughs, narration and practice run on `BookClock` (the system clock, unless a recording holds it
  and steps it a frame at a time: `BeginStepping`, `Advance`, `EndStepping`); a book's own animations can use its
  `BookStopwatch` too, so they film smoothly. A recording that cannot wait for a viewer shortens a walkthrough's
  pauses (`LessonPlayer.AnswerSeconds`, `TryItSeconds`, `ResumeSeconds`) and names its audio folder with
  `NarrationService.CultureFolder`.
- **Diagrams**: an interactive diagram keeps room at its bottom for the walkthrough's subtitles with `LessonReserve`.
- **Practice**: `PracticeAutopilot.RevealAfterSeconds` is the book's wait before an unanswered question shows its answer
  (5 by default); the reader can turn that off (`PracticeAutopilot.Load` and `SetRevealsAnswers`, the
  `practiceRevealsAnswers` setting).
- **Web**: `main.js` imports `./_content/Lemon.Book.Shell.Browser/download-progress.js` and `wake-lock.js`;
  `Program.cs` imports `WebNarrationAudio.ModuleName` from `WebNarrationAudio.ModuleUrl`; `sw.js` lists the three
  scripts in its shell.

## Changing the shell

```bash
dotnet test
scripts/pack.sh ../Lemon.English.Book
```

Bump `<Version>` in `Directory.Build.props` for every change the books should get. To publish a version to
nuget.org, with `NUGET_API_KEY` set in the environment (never in a file), from a clean, committed tree:

```bash
scripts/push-nuget.sh --dry-run
scripts/push-nuget.sh
```

It stops when the version is already on nuget.org.

The books still to move onto the
package (the Picture, Maths and Explore books) carry their own copy of the shell until they do.

## Licence

MIT.
