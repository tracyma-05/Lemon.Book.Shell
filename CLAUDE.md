# Lemon.Book.Shell

The shell the Lemon books share, as three NuGet packages (`src/Lemon.Book.Shell`, `.Desktop`, `.Browser`), Avalonia
12 and .NET 10. Extracted from Lemon.Picture.Book; Lemon.English.Book is built on it, and the Picture, Maths and
Explore books are to move onto it one at a time. Using it and packing it: `README.md`.

## Public repository: nothing sensitive, ever

- No credentials, tokens, private host names, internal product names, e-mail addresses or personal data in
  code, comments, strings, docs or commit messages. Use obvious placeholders in examples.
- No sync server or update feed is ever built into a package: they are the book's build metadata
  (`BookShell.Metadata`), read from the book's assembly.
- `scripts/check-sensitive.sh` runs as the pre-commit hook (`git config core.hooksPath .githooks`). Keep it passing;
  never bypass the hook.

## What belongs here

- What every book has: the book and its page turn, narration, lessons, bookmarks, notes, settings, localization,
  themes, updates, sync. A book's own pages, its contents, its kinds of progress and its look beyond the shared one
  stay in the book.
- Nothing names a book. What differs between books is a `BookShellOptions` value or an interface the book
  implements (`ISyncRules`), never a switch on the book.
- The packages must stay AOT- and trim-safe (`IsAotCompatible`): the books publish with Native AOT and
  WebAssembly AOT.

## Strings

- The shell's strings are in `src/Lemon.Book.Shell/Resources/ShellStrings.resx` (Chinese). A book's own
  `AppStrings.resx` comes first and may reword any of them. Narration lines (`Narr_*`) are never here: each book
  records its own.

## Before committing

```bash
dotnet test
```

Then bump `<Version>` in `Directory.Build.props` and run `scripts/pack.sh <book>` for each book that should get the
change.
