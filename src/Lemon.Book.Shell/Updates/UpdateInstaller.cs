using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

using Lemon.Book.Shell.Infrastructures.Localization;

namespace Lemon.Book.Shell.Updates;

/// <summary>Bytes received so far, and the total when the server said.</summary>
public readonly record struct UpdateDownloadProgress(long Received, long? Total)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;
}

/// <summary>An update downloaded, verified and unpacked, waiting for <see cref="UpdateInstaller.Launch"/>.</summary>
/// <param name="NewRoot">The new version in the staging folder (the app folder or .app bundle).</param>
public sealed record PreparedUpdate(AppInstallation Installation, string NewRoot, Version Version);

/// <summary>Preparing the update failed; the message is for the reader.</summary>
public sealed class UpdateInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How the last update went, read by the new version when it starts.</summary>
/// <param name="Failed">The updater reported a failure (the old version was kept).</param>
/// <param name="Log">The updater's whole log.</param>
public sealed record UpdateCleanupResult(bool Failed, string Log);

/// <summary>
/// The first half of an update: downloads the package to a work folder next to the app, verifies and unpacks it,
/// then starts the new version as the updater and lets the app exit. The second half is <see cref="UpdateApplier"/>.
/// </summary>
public sealed class UpdateInstaller
{
    // No overall timeout for a download of unknown size; it stops when no byte arrives for a while instead.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly Lazy<HttpClient> SharedClient = new(() => UpdateService.CreateClient(Timeout.InfiniteTimeSpan, acceptJson: false));

    private static readonly string[] InstallerExtensions = [".msi", ".exe", ".dmg", ".pkg", ".msix", ".appx", ".appinstaller"];

    private readonly HttpClient _httpClient;
    private readonly AppInstallation? _installation;

    public UpdateInstaller()
        : this(SharedClient.Value, AppInstallation.Current)
    {
    }

    /// <summary>For tests: a fake transport, and an "installation" in a temporary folder.</summary>
    internal UpdateInstaller(HttpClient httpClient, AppInstallation? installation)
    {
        _httpClient = httpClient;
        _installation = installation;
    }

    /// <summary>
    /// Whether the update can be installed in place: the package is a zip (not an installer or a web page) and the
    /// app folder and its parent are writable. Otherwise the reader downloads it in the browser.
    /// </summary>
    public bool CanInstall(UpdateCheckResult result) => WhyNotInstallable(result) is null;

    /// <summary>Why the update cannot be installed automatically (for logs and tests); null when it can.</summary>
    public string? WhyNotInstallable(UpdateCheckResult result)
    {
        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.DownloadUrl is not { } url || result.Latest is null)
        {
            return "no update";
        }

        if (url == result.Latest.PageUrl)
        {
            return "the release has no package for this platform";
        }

        var extension = Path.GetExtension(url.AbsolutePath);
        if (InstallerExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return $"the package is an installer ({extension})";
        }

        if (_installation is null)
        {
            return "the app runs through the dotnet host";
        }

        return IsWritable(_installation) ? null : $"{_installation.RootPath} or its parent folder is not writable";
    }

    /// <summary>Downloads the package, verifies it when the release gives a checksum, and unpacks it next to the app.</summary>
    /// <exception cref="UpdateInstallException">The download or the package is not usable.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    public async Task<PreparedUpdate> PrepareAsync(
        UpdateCheckResult result, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (WhyNotInstallable(result) is { } reason)
        {
            throw new InvalidOperationException($"This update cannot be installed automatically: {reason}.");
        }

        var installation = _installation!;
        var work = installation.WorkPath;
        var package = Path.Combine(work, "package.zip");
        var staging = Path.Combine(work, "staging");

        try
        {
            ResetFolder(work);

            await DownloadAsync(result.DownloadUrl!, package, progress, cancellationToken).ConfigureAwait(false);

            if (result.Latest!.Checksums.TryGetValue(result.DownloadUrl!, out var expected))
            {
                var actual = await HashAsync(package, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateInstallException(LocalizationService.Instance.GetString("Update_Error_Damaged"));
                }
            }

            if (!IsZip(package))
            {
                throw new UpdateInstallException(LocalizationService.Instance.GetString("Update_Error_NotZip"));
            }

            await Task.Run(() => Extract(package, staging), cancellationToken).ConfigureAwait(false);
            File.Delete(package);

            var newRoot = installation.FindIn(staging)
                          ?? throw new UpdateInstallException(
                              LocalizationService.Instance.Format("Update_Error_NotThisApp", Path.GetFileName(installation.ExecutableRelativePath)));

            if (!OperatingSystem.IsWindows())
            {
                // Some zip tools drop the executable bit.
                var executable = Path.Combine(newRoot, installation.ExecutableRelativePath);
                File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            }

            return new PreparedUpdate(installation, newRoot, result.Latest.Version);
        }
        catch (Exception ex) when (ex is not UpdateInstallException)
        {
            TryDeleteFolder(work);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new UpdateInstallException(
                ex is HttpRequestException or TimeoutException
                    ? LocalizationService.Instance.GetString("Update_Error_Interrupted")
                    : LocalizationService.Instance.Format("Update_Error_Install", ex.Message),
                ex);
        }
        catch
        {
            TryDeleteFolder(work);
            throw;
        }
    }

    /// <summary>
    /// Starts the new version as the updater (<see cref="UpdateApplier"/>). The caller must then exit the app soon: the
    /// updater waits for this process to end before it swaps the files.
    /// </summary>
    public void Launch(PreparedUpdate update)
    {
        var start = new ProcessStartInfo(Path.Combine(update.NewRoot, update.Installation.ExecutableRelativePath))
        {
            UseShellExecute = false,
            // Not the app folder: a process with its working directory inside it would block the rename.
            WorkingDirectory = update.Installation.WorkPath,
        };
        foreach (var argument in UpdateApplier.BuildArguments(Environment.ProcessId, update.Installation, update.NewRoot))
        {
            start.ArgumentList.Add(argument);
        }

        using var _ = Process.Start(start) ?? throw new UpdateInstallException(LocalizationService.Instance.GetString("Update_Error_Launch"));
    }

    /// <summary>
    /// Called once the app is up: reads how the updater did, and deletes the work folder (backup, staging, log). The
    /// updater may still be exiting, so this retries for a while.
    /// </summary>
    /// <returns>The result right after an update; null on a normal start.</returns>
    public static async Task<UpdateCleanupResult?> CleanUpAfterUpdateAsync(AppInstallation? installation = null)
    {
        installation ??= AppInstallation.Current;
        if (installation is null || !Directory.Exists(installation.WorkPath))
        {
            return null;
        }

        UpdateCleanupResult? result = null;
        var logFile = Path.Combine(installation.WorkPath, UpdateApplier.LogFileName);
        try
        {
            if (File.Exists(logFile))
            {
                var text = await File.ReadAllTextAsync(logFile).ConfigureAwait(false);
                result = new UpdateCleanupResult(text.Contains("FAILED", StringComparison.Ordinal), text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                Directory.Delete(installation.WorkPath, recursive: true);
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }

        return result;
    }

    private async Task DownloadAsync(Uri url, string path, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateInstallException(LocalizationService.Instance.Format("Update_Error_Download", (int)response.StatusCode));
            }

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            progress?.Report(new UpdateDownloadProgress(0, total));
            int read;
            while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                received += read;
                progress?.Report(new UpdateDownloadProgress(received, total));
                stall.CancelAfter(StallTimeout);
            }

            if (total is { } expected && received != expected)
            {
                throw new UpdateInstallException(LocalizationService.Instance.GetString("Update_Error_Interrupted"));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The stall timer ended it, not the reader.
            throw new TimeoutException($"No data from {url} for {StallTimeout.TotalSeconds:0} s.");
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static bool IsZip(string path)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) == 4 &&
               header[0] == (byte)'P' && header[1] == (byte)'K' && header[2] == 3 && header[3] == 4;
    }

    private static void Extract(string package, string staging)
    {
        if (OperatingSystem.IsMacOS())
        {
            // ditto restores the symbolic links, permissions and bundle signature `ditto -c -k` stored.
            using var ditto = Process.Start(new ProcessStartInfo("/usr/bin/ditto") { ArgumentList = { "-x", "-k", package, staging } })
                              ?? throw new IOException("Could not start ditto.");
            ditto.WaitForExit();
            if (ditto.ExitCode != 0)
            {
                throw new IOException($"ditto exited with code {ditto.ExitCode}.");
            }

            return;
        }

        // Refuses entries that would land outside the staging folder ("../" paths).
        ZipFile.ExtractToDirectory(package, staging);
    }

    private static bool IsWritable(AppInstallation installation)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installation.RootPath));
        return parent is not null && CanWriteIn(installation.RootPath) && CanWriteIn(parent);
    }

    private static bool CanWriteIn(string folder)
    {
        var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ResetFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void TryDeleteFolder(string path)
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
        }
    }
}
