namespace Lemon.Book.Shell.Updates;

/// <summary>
/// The folder an update replaces and the executable inside it: the app folder on Windows, the <c>.app</c>
/// bundle on macOS (the executable is then <c>Contents/MacOS/&lt;name&gt;</c>).
/// </summary>
/// <param name="RootPath">The installed app folder or bundle.</param>
/// <param name="ExecutableRelativePath">The executable, relative to <paramref name="RootPath"/>.</param>
public sealed record AppInstallation(string RootPath, string ExecutableRelativePath)
{
    public string ExecutablePath => Path.Combine(RootPath, ExecutableRelativePath);

    public bool IsMacBundle => RootPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Download, staging and backup area next to the installation: on the same volume, so the swap is a
    /// rename, and outside it, so it survives the swap. Removed again once the new version has started.
    /// </summary>
    public string WorkPath => Path.Combine(
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(RootPath)) ?? RootPath,
        "." + Path.GetFileName(Path.TrimEndingDirectorySeparator(RootPath)) + ".update");

    /// <summary>
    /// The running app, or null when it cannot replace itself: started through the <c>dotnet</c> host
    /// (<c>dotnet App.dll</c>), or the process path is unknown.
    /// </summary>
    public static AppInstallation? Current { get; } = Detect(Environment.ProcessPath);

    /// <summary>
    /// Called at start-up: moves the working directory out of the installation (to the home folder). A process whose
    /// working directory is inside the installation holds it open, and an update could not rename the folder.
    /// </summary>
    public static void MoveWorkingDirectoryOut()
    {
        try
        {
            var current = Path.GetFullPath(Environment.CurrentDirectory);
            var root = Current?.RootPath ?? AppContext.BaseDirectory;
            if (!current.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Environment.CurrentDirectory = Directory.Exists(home) ? home : Path.GetTempPath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Harmless: an update then falls back to replacing the files one by one.
        }
    }

    internal static AppInstallation? Detect(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath) ||
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(processPath);
        if (folder is null)
        {
            return null;
        }

        // …/Name.app/Contents/MacOS/Name: the bundle is what gets replaced, never just its MacOS folder.
        var contents = Path.GetDirectoryName(folder);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        if (bundle is not null &&
            string.Equals(Path.GetFileName(folder), "MacOS", StringComparison.Ordinal) &&
            string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal) &&
            bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return new AppInstallation(bundle, Path.GetRelativePath(bundle, processPath));
        }

        return new AppInstallation(folder, Path.GetFileName(processPath));
    }

    /// <summary>
    /// Where this app sits inside an extracted package: the package root, or a folder up to two levels down
    /// (a zip made of the folder rather than its contents, or <c>ditto --keepParent</c>'s <c>Name.app</c>),
    /// that holds the same executable. Null when the package is not a build of this app.
    /// </summary>
    public string? FindIn(string extractedPath)
    {
        foreach (var candidate in Candidates(extractedPath, depth: 2))
        {
            if (IsMacBundle && !candidate.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(Path.Combine(candidate, ExecutableRelativePath)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string folder, int depth)
    {
        yield return folder;
        if (depth == 0)
        {
            yield break;
        }

        foreach (var child in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            foreach (var candidate in Candidates(child, depth - 1))
            {
                yield return candidate;
            }
        }
    }
}
