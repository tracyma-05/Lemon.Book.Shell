using System.Diagnostics;
using System.Globalization;

namespace Lemon.Book.Shell.Updates;

/// <summary>
/// The second half of an update: run by the <em>new</em> version from the staging folder, it waits for the old one to
/// exit and swaps the installation (the first half is <see cref="UpdateInstaller"/>). Checked first thing at
/// start-up, before any window; when done it starts the freshly installed app and exits.
/// </summary>
/// <remarks>
/// The swap never leaves a half-installed app: the installation is renamed to a backup, the new files copied in,
/// and whatever only the old installation had (files the package does not carry) <em>moved</em> back from the backup,
/// the same as unpacking the new package over the old folder. Moved, not copied, and each move is recorded so it can
/// be undone. Any failure deletes the half-installed folder, renames the backup back and starts the app anyway: the
/// reader always keeps a working version.
/// </remarks>
public static class UpdateApplier
{
    public const string Argument = "--apply-update";

    /// <summary>Changes only with the argument format. The old version starts the new one's updater, so new versions must read old formats.</summary>
    private const string ProtocolVersion = "1";

    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private const int Attempts = 20; // about 10 s: antivirus and indexers hold files briefly after a process exits

    public const string LogFileName = "update.log";

    /// <summary>The arguments <see cref="TryRun"/> reads, in order.</summary>
    internal static IReadOnlyList<string> BuildArguments(int processId, AppInstallation installation, string newRoot) =>
    [
        Argument,
        ProtocolVersion,
        processId.ToString(CultureInfo.InvariantCulture),
        installation.RootPath,
        newRoot,
        installation.ExecutableRelativePath,
        installation.WorkPath,
    ];

    /// <summary>
    /// When the process was started as the updater: applies the update and returns true (the caller exits with
    /// <paramref name="exitCode"/>); otherwise returns false at once.
    /// </summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != Argument)
        {
            return false;
        }

        if (args.Length != 7 || args[1] != ProtocolVersion ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
        {
            exitCode = 2;
            return true;
        }

        var installation = new AppInstallation(args[3], args[5]);
        exitCode = Run(processId, installation, newRoot: args[4], workPath: args[6]);
        return true;
    }

    private static int Run(int processId, AppInstallation installation, string newRoot, string workPath)
    {
        var log = new UpdateLog(Path.Combine(workPath, LogFileName));
        try
        {
            log.Write($"Updating {installation.RootPath} from {newRoot}.");
            if (!WaitForExit(processId))
            {
                // Nothing was touched: the old version is still running and still works.
                log.Write($"FAILED: process {processId} did not exit within {ExitTimeout.TotalSeconds:0} s.");
                return 1;
            }

            var backup = Path.Combine(workPath, "backup");
            try
            {
                var inPlace = Apply(installation.RootPath, newRoot, backup);
                log.Write(inPlace ? "Installed (the folder was in use, files were replaced in place)." : "Installed.");
            }
            catch (Exception ex)
            {
                log.Write($"FAILED, the previous version was kept: {ex}");
                Start(installation);
                return 1;
            }

            Start(installation);
            TryDelete(backup);
            return 0;
        }
        catch (Exception ex)
        {
            log.Write($"FAILED: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Replaces <paramref name="installRoot"/> with <paramref name="newRoot"/>, keeping what only the old installation
    /// had. On any problem the old installation is restored before the exception is thrown.
    /// </summary>
    /// <param name="backupPath">Where the old installation is renamed to; afterwards only the replaced files remain, for the caller to delete.</param>
    /// <param name="copyFile">For tests: copies one file (source, destination).</param>
    /// <param name="moveEntry">For tests: moves back one entry only the old installation had (source, destination).</param>
    /// <returns>True when the folder was in use and the files were replaced one by one.</returns>
    internal static bool Apply(
        string installRoot,
        string newRoot,
        string backupPath,
        Action<string, string>? copyFile = null,
        Action<string, string>? moveEntry = null)
    {
        if (Directory.Exists(backupPath))
        {
            Directory.Delete(backupPath, recursive: true);
        }

        // The rename fails (with nothing changed yet) while a process has files in the folder open, or uses it as its
        // working directory. The files inside can still be overwritten, so fall back to that.
        try
        {
            Retry(() => Directory.Move(installRoot, backupPath), attempts: 6);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ApplyInPlace(installRoot, newRoot, backupPath, copyFile ?? CopyFile);
            return true;
        }

        var carried = new List<(string From, string To)>();
        try
        {
            if (copyFile is null && OperatingSystem.IsMacOS())
            {
                // ditto keeps the .app bundle's symbolic links, permissions, extended attributes and signature.
                RunDitto(newRoot, installRoot);
            }
            else
            {
                CopyTree(newRoot, installRoot, copyFile ?? CopyFile);
            }

            CarryOver(backupPath, installRoot, carried, moveEntry ?? MoveEntry);
        }
        catch
        {
            // Put the moved entries back into the backup, then swap the whole backup back in.
            for (var i = carried.Count - 1; i >= 0; i--)
            {
                var (from, to) = carried[i];
                Retry(() => MoveEntry(to, from));
            }

            if (Directory.Exists(installRoot))
            {
                Retry(() => Directory.Delete(installRoot, recursive: true));
            }

            Retry(() => Directory.Move(backupPath, installRoot));
            throw;
        }

        return false;
    }

    /// <summary>
    /// The fallback when the folder cannot be renamed: each file of the package overwrites the installed one, after the
    /// old file is copied to the backup. Files the package does not have are left alone. On failure the added files
    /// are deleted and the backed-up ones copied back. Old files the new version no longer ships stay behind, unused.
    /// </summary>
    private static void ApplyInPlace(string installRoot, string newRoot, string backupPath, Action<string, string> copyFile)
    {
        var replaced = new List<string>();
        var added = new List<string>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(newRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(newRoot, source);
                var destination = Path.Combine(installRoot, relative);
                if (File.Exists(destination))
                {
                    var saved = Path.Combine(backupPath, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved, overwrite: true);
                    replaced.Add(relative);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    added.Add(relative);
                }

                Retry(() => copyFile(source, destination));
            }
        }
        catch
        {
            foreach (var relative in added)
            {
                TryDeleteFile(Path.Combine(installRoot, relative));
            }

            foreach (var relative in replaced)
            {
                var saved = Path.Combine(backupPath, relative);
                Retry(() => File.Copy(saved, Path.Combine(installRoot, relative), overwrite: true));
            }

            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Copies all of <paramref name="source"/> into <paramref name="target"/>, overwriting files of the same name.</summary>
    private static void CopyTree(string source, string target, Action<string, string> copyFile)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var destination = Path.Combine(target, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                // The framework links inside an .app bundle stay links rather than becoming copies.
                if (!Path.Exists(destination))
                {
                    if (entry is DirectoryInfo)
                    {
                        Directory.CreateSymbolicLink(destination, link);
                    }
                    else
                    {
                        File.CreateSymbolicLink(destination, link);
                    }
                }
            }
            else if (entry is DirectoryInfo)
            {
                CopyTree(entry.FullName, destination, copyFile);
            }
            else
            {
                copyFile(entry.FullName, destination);
            }
        }
    }

    /// <summary>
    /// Moves what the old installation has and the new one does not back into the new one: a folder both have is
    /// compared one level down, a folder only the old one has is moved whole (a rename on the same volume). Files the
    /// new package carries win.
    /// </summary>
    private static void CarryOver(string backup, string install, List<(string From, string To)> carried, Action<string, string> move)
    {
        foreach (var entry in new DirectoryInfo(backup).EnumerateFileSystemInfos())
        {
            var destination = Path.Combine(install, entry.Name);
            if (entry is DirectoryInfo && entry.LinkTarget is null && Directory.Exists(destination) && new DirectoryInfo(destination).LinkTarget is null)
            {
                CarryOver(entry.FullName, destination, carried, move);
            }
            else if (!Path.Exists(destination) && !File.Exists(destination))
            {
                move(entry.FullName, destination);
                carried.Add((entry.FullName, destination));
            }
        }
    }

    private static void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: true);

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source) && !File.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void RunDitto(string source, string target)
    {
        using var ditto = Process.Start(new ProcessStartInfo("/usr/bin/ditto") { ArgumentList = { source, target } })
                          ?? throw new IOException("Could not start ditto.");
        ditto.WaitForExit();
        if (ditto.ExitCode != 0)
        {
            throw new IOException($"ditto exited with code {ditto.ExitCode}.");
        }
    }

    private static bool WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(ExitTimeout);
        }
        catch (ArgumentException)
        {
            return true; // already exited
        }
    }

    /// <summary>Starts the installed app; an .app bundle through LaunchServices (open), so the Dock shows it normally.</summary>
    internal static void Start(AppInstallation installation)
    {
        var start = installation.IsMacBundle
            ? new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-n", installation.RootPath } }
            : new ProcessStartInfo(installation.ExecutablePath) { WorkingDirectory = installation.RootPath };
        start.UseShellExecute = false;
        Process.Start(start)?.Dispose();
    }

    private static void Retry(Action action, int attempts = Attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The new version deletes the whole work folder when it starts.
        }
    }

    /// <summary>A plain text log next to the backup: the updater has no other logging.</summary>
    private sealed class UpdateLog(string path)
    {
        public void Write(string message)
        {
            try
            {
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must never fail the update.
            }
        }
    }
}
