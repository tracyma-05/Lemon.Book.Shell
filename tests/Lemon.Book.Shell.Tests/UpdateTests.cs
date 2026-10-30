using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.Infrastructures.Settings;
using Lemon.Book.Shell.Updates;
using Xunit;

namespace Lemon.Book.Shell.Tests;

/// <summary>
/// Updates: the address and its three levels, reading a manifest or a GitHub release, finding the app in a package,
/// swapping the installation (never half-installed, never losing files), and the banner's state.
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private const string Exe = "PictureBook.exe";
    private static readonly Uri PackageUrl = new("https://downloads.example.com/picture-book/picture-book-0.2.0-win-x64.zip");
    private static readonly string Sha = new('a', 64);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "book-update-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- the address

    [Theory]
    [InlineData("github:owner/picture-book", true, "https://api.github.com/repos/owner/picture-book/releases/latest")]
    [InlineData("https://github.com/owner/picture-book.git", true, "https://api.github.com/repos/owner/picture-book/releases/latest")]
    [InlineData("https://downloads.example.com/picture-book/latest.json", false, "https://downloads.example.com/picture-book/latest.json")]
    public void AFeed_IsAGitHubRepository_OrAManifestAddress(string feed, bool github, string address)
    {
        var source = UpdateFeed.Parse(feed)!;

        Assert.Equal(github, source.IsGitHub);
        Assert.Equal(address, source.Address.AbsoluteUri);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("")]
    [InlineData("ftp://downloads.example.com/latest.json")]
    [InlineData("github:not a repo")]
    public void Off_Empty_AndNonsense_ReadNothing(string feed)
    {
        Assert.Null(UpdateFeed.Parse(feed));
        Assert.Equal(feed is "off" or "", UpdateFeed.IsValid(feed));
    }

    [Fact]
    public void TheReadersAddress_BeatsTheInstallFolder_WhichBeatsTheBuiltInOne()
    {
        var settings = new MemorySettingsStore();
        Assert.Equal(UpdateFeedOrigin.BuiltIn, UpdateFeed.Resolve(settings, _root).Origin);

        File.WriteAllText(Path.Combine(_root, UpdateFeed.FileName), "# packaged for our school\n\nhttps://school.example.com/book/latest.json\n");
        Assert.Equal(("https://school.example.com/book/latest.json", UpdateFeedOrigin.InstallFolder), UpdateFeed.Resolve(settings, _root));

        settings.Set(UpdateFeed.SettingKey, "github:someone/fork");
        Assert.Equal(("github:someone/fork", UpdateFeedOrigin.Reader), UpdateFeed.Resolve(settings, _root));
    }

    [Fact]
    public void TheSettings_SaveAnAddress_RejectNonsense_AndRestoreTheDefault()
    {
        var settings = new MemorySettingsStore();
        File.WriteAllText(Path.Combine(_root, UpdateFeed.FileName), "github:packager/book");
        var update = CreateViewModel(settings, _ => throw new InvalidOperationException());
        Assert.Equal("github:packager/book", update.FeedText);

        update.FeedText = "not an address";
        Assert.NotNull(update.FeedError);
        update.SaveFeedCommand.Execute(null);
        Assert.Null(settings.Get(UpdateFeed.SettingKey));

        update.FeedText = "https://mirror.example.com/latest.json";
        update.SaveFeedCommand.Execute(null);
        Assert.Equal("https://mirror.example.com/latest.json", settings.Get(UpdateFeed.SettingKey));
        Assert.True(update.HasReaderFeed);

        update.ResetFeedCommand.Execute(null);
        Assert.Equal("github:packager/book", update.FeedText);
        Assert.False(update.HasReaderFeed);
    }

    // ---------------------------------------------------------------- reading a release

    [Fact]
    public void AGitHubRelease_FilesEachPackageUnderItsPlatform_AndKeepsTheDigests()
    {
        var manifest = UpdateService.ParseGitHubRelease($$"""
            {
              "tag_name": "v0.2.0",
              "html_url": "https://github.com/owner/picture-book/releases/tag/v0.2.0",
              "body": "- New chapters",
              "assets": [
                { "name": "picture-book-0.2.0-win-x64.zip", "browser_download_url": "https://github.com/d/win.zip", "digest": "sha256:{{Sha}}" },
                { "name": "picture-book-0.2.0-osx-arm64.zip", "browser_download_url": "https://github.com/d/arm.zip" },
                { "name": "picture-book-0.2.0-osx-x64.zip", "browser_download_url": "https://github.com/d/x64.zip" },
                { "name": "picture-book-0.2.0-web.zip", "browser_download_url": "https://github.com/d/web.zip" }
              ]
            }
            """)!;

        Assert.Equal(new Version(0, 2, 0, 0), manifest.Version);
        Assert.Equal(["osx-arm64", "osx-x64", "win-x64"], manifest.Downloads.Keys.Order());
        Assert.Equal(Sha, manifest.Checksums[new Uri("https://github.com/d/win.zip")]);
        Assert.Equal("- New chapters", manifest.ReleaseNotes);
    }

    [Theory]
    [InlineData("osx-arm64", "https://downloads.example.com/arm.zip")]
    [InlineData("osx-x64", "https://downloads.example.com/x64.zip")]
    // A native AOT build is self-contained: without -full packages it takes the plain ones.
    [InlineData("win-x64-full", "https://downloads.example.com/win.zip")]
    public async Task EachComputer_GetsItsOwnPackage(string platform, string expected)
    {
        var client = new HttpClient(new StubHandler(_ => Json("""
            {
              "version": "0.2.0",
              "downloads": {
                "win-x64": "https://downloads.example.com/win.zip",
                "osx-arm64": "https://downloads.example.com/arm.zip",
                "osx-x64": "https://downloads.example.com/x64.zip"
              }
            }
            """)));
        var service = new UpdateService(client, new Version(0, 1, 0, 0), platform);

        var result = await service.CheckAsync(UpdateFeed.Parse("https://downloads.example.com/latest.json"), TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(expected, result.DownloadUrl!.AbsoluteUri);
    }

    [Fact]
    public async Task NoRelease_Offline_AndNoAddress_AreFailuresWithAReason()
    {
        var localization = LocalizationService.Instance;
        var notFound = new UpdateService(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), new Version(0, 1, 0, 0));
        Assert.Equal(localization.GetString("Update_Error_NoRelease"),
            (await notFound.CheckAsync(UpdateFeed.Parse("github:owner/book"), TestContext.Current.CancellationToken)).Error);

        var offline = new UpdateService(new HttpClient(new StubHandler(_ => throw new HttpRequestException("no route"))), new Version(0, 1, 0, 0));
        Assert.Equal(localization.GetString("Update_Error_Offline"),
            (await offline.CheckAsync(UpdateFeed.Parse("github:owner/book"), TestContext.Current.CancellationToken)).Error);

        Assert.Equal(localization.GetString("Update_Error_NoFeed"),
            (await offline.CheckAsync(null, TestContext.Current.CancellationToken)).Error);
    }

    [Fact]
    public void AManifest_ReadsPerPackageChecksums()
    {
        var manifest = UpdateService.ParseManifest(
            $$"""
            {
              "version": "0.2.0",
              "downloads": { "win-x64": { "url": "win.zip", "sha256": "{{Sha.ToUpperInvariant()}}" }, "osx-arm64": "mac.zip" },
              "sha256": "{{new string('b', 64)}}"
            }
            """,
            new Uri("https://downloads.example.com/book/latest.json"))!;

        Assert.Equal(new Uri("https://downloads.example.com/book/win.zip"), manifest.Downloads["win-x64"]);
        Assert.Equal(Sha, manifest.Checksums[manifest.Downloads["win-x64"]]);
        Assert.False(manifest.Checksums.ContainsKey(manifest.Downloads["osx-arm64"]));
    }

    // ---------------------------------------------------------------- where the app is installed

    [Fact]
    public void TheInstallation_IsTheExeFolderOnWindows_AndTheBundleOnMacOS()
    {
        var windows = AppInstallation.Detect(Path.Combine(_root, "Picture Book", Exe))!;
        Assert.Equal(Path.Combine(_root, "Picture Book"), windows.RootPath);
        Assert.Equal(Path.Combine(_root, ".Picture Book.update"), windows.WorkPath);

        var mac = AppInstallation.Detect(Path.Combine(_root, "Picture Book.app", "Contents", "MacOS", "PictureBook"))!;
        Assert.Equal(Path.Combine(_root, "Picture Book.app"), mac.RootPath);
        Assert.True(mac.IsMacBundle);

        Assert.Null(AppInstallation.Detect(Path.Combine(_root, "dotnet.exe")));
    }

    // ---------------------------------------------------------------- swapping the installation

    [Fact]
    public void Apply_InstallsThePackage_AndMovesWhatOnlyTheOldOneHadBack()
    {
        var install = Path.Combine(_root, "Picture Book");
        WriteFile(Path.Combine(install, Exe), "old exe");
        WriteFile(Path.Combine(install, UpdateFeed.FileName), "https://school.example.com/book/latest.json");

        var staged = Path.Combine(_root, "staging");
        WriteFile(Path.Combine(staged, Exe), "new exe");

        var backup = Path.Combine(_root, "backup");
        UpdateApplier.Apply(install, staged, backup, CopyFile);

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, Exe)));

        // A packager's update address survives the update, moved rather than copied.
        Assert.Equal("https://school.example.com/book/latest.json", File.ReadAllText(Path.Combine(install, UpdateFeed.FileName)));
        Assert.False(File.Exists(Path.Combine(backup, UpdateFeed.FileName)));
    }

    [Fact]
    public void Apply_RestoresTheOldInstallation_WhenCopyingFails()
    {
        var install = Path.Combine(_root, "Picture Book");
        WriteFile(Path.Combine(install, Exe), "old exe");

        var staged = Path.Combine(_root, "staging");
        WriteFile(Path.Combine(staged, Exe), "new exe");
        WriteFile(Path.Combine(staged, "Broken.dll"), "x");

        Assert.Throws<IOException>(() => UpdateApplier.Apply(install, staged, Path.Combine(_root, "backup"),
            (source, destination) =>
            {
                if (source.EndsWith("Broken.dll", StringComparison.Ordinal)) throw new IOException("disk full");
                CopyFile(source, destination);
            }));

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, Exe)));
        Assert.False(File.Exists(Path.Combine(install, "Broken.dll")));
    }

    [Fact]
    public void TryRun_IgnoresANormalStart_AndRejectsUnknownProtocols()
    {
        Assert.False(UpdateApplier.TryRun([], out _));

        Assert.True(UpdateApplier.TryRun([UpdateApplier.Argument, "99", "1", "a", "b", "c", "d"], out var exitCode));
        Assert.Equal(2, exitCode);
    }

    // ---------------------------------------------------------------- downloading, verifying, unpacking

    [Theory]
    [InlineData("https://downloads.example.com/picture-book-0.2.0-win-x64.zip", true)]
    [InlineData("https://downloads.example.com/picture-book-0.2.0-win-x64.msi", false)]
    public void OnlyAZip_InstallsInPlace(string url, bool expected)
    {
        var installer = CreateInstaller(_ => throw new InvalidOperationException());

        Assert.Equal(expected, installer.CanInstall(Available(new Uri(url))));
    }

    [Fact]
    public async Task PrepareAsync_Downloads_Verifies_AndUnpacksThePackage()
    {
        var zip = ZipOf((Exe, "new exe"), ("libSkiaSharp.dll", "skia"));
        var installer = CreateInstaller(_ => Bytes(zip));

        var prepared = await installer.PrepareAsync(Available(PackageUrl, Hash(zip)), null, TestContext.Current.CancellationToken);

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(prepared.NewRoot, Exe)));
        Assert.False(File.Exists(Path.Combine(Installation.WorkPath, "package.zip")));
    }

    [Fact]
    public async Task PrepareAsync_RejectsADamagedDownload_ASignInPage_AndAnotherApp()
    {
        var localization = LocalizationService.Instance;
        var damaged = CreateInstaller(_ => Bytes(ZipOf((Exe, "new exe"))));
        var error = await Assert.ThrowsAsync<UpdateInstallException>(() =>
            damaged.PrepareAsync(Available(PackageUrl, Sha), null, TestContext.Current.CancellationToken));
        Assert.Equal(localization.GetString("Update_Error_Damaged"), error.Message);
        Assert.False(Directory.Exists(Installation.WorkPath));

        var html = CreateInstaller(_ => Bytes("<html>sign in</html>"u8.ToArray()));
        Assert.Equal(localization.GetString("Update_Error_NotZip"), (await Assert.ThrowsAsync<UpdateInstallException>(() =>
            html.PrepareAsync(Available(PackageUrl), null, TestContext.Current.CancellationToken))).Message);

        var other = CreateInstaller(_ => Bytes(ZipOf(("SomethingElse.exe", "x"))));
        Assert.Contains(Exe, (await Assert.ThrowsAsync<UpdateInstallException>(() =>
            other.PrepareAsync(Available(PackageUrl), null, TestContext.Current.CancellationToken))).Message);
    }

    [Fact]
    public async Task CleanUp_ReportsTheUpdatersResult_AndRemovesTheWorkFolder()
    {
        WriteFile(Path.Combine(Installation.WorkPath, UpdateApplier.LogFileName), "Updating…\nFAILED, the previous version was kept: locked");

        var result = await UpdateInstaller.CleanUpAfterUpdateAsync(Installation);

        Assert.True(result!.Failed);
        Assert.False(Directory.Exists(Installation.WorkPath));
        Assert.Null(await UpdateInstaller.CleanUpAfterUpdateAsync(Installation));
    }

    // ---------------------------------------------------------------- the banner

    [Fact]
    public async Task TheStartupCheck_ShowsTheBanner_OnlyForANewerVersion()
    {
        var settings = new MemorySettingsStore();
        settings.Set(UpdateFeed.SettingKey, "https://downloads.example.com/latest.json");
        var manifest = $$"""{ "version": "0.2.0", "downloads": { "win-x64": "{{PackageUrl}}" }, "releaseNotes": "- New chapters" }""";
        var update = CreateViewModel(settings, _ => Json(manifest));

        await update.CheckOnStartupAsync();

        Assert.True(update.ShowsBanner);
        Assert.True(update.CanInstall);
        Assert.Equal(LocalizationService.Instance.GetString("Update_InstallAndRestart"), update.PrimaryButtonText);
        Assert.Equal("0.2.0", update.LatestVersion);
        Assert.Equal("- New chapters", update.ReleaseNotes);

        update.DismissCommand.Execute(null);
        Assert.False(update.ShowsBanner);

        var upToDate = CreateViewModel(settings, _ => Json("""{ "version": "0.1.0", "pageUrl": "https://example.com" }"""));
        await upToDate.CheckOnStartupAsync();
        Assert.False(upToDate.ShowsBanner);

        settings.Set(UpdateFeed.CheckOnStartupSetting, bool.FalseString);
        var quiet = CreateViewModel(settings, _ => throw new InvalidOperationException("must not ask"));
        await quiet.CheckOnStartupAsync();
        Assert.False(quiet.ShowsBanner);
    }

    [Fact]
    public void TheWebEdition_HasNoUpdates()
    {
        var update = new UpdateViewModel(new MemorySettingsStore(), () => { },
            new UpdateService(new HttpClient(new StubHandler(_ => throw new InvalidOperationException())), new Version(0, 1, 0, 0)),
            new UpdateInstaller(new HttpClient(new StubHandler(_ => throw new InvalidOperationException())), null),
            installFolder: null);

        Assert.False(update.IsSupported);
        Assert.False(update.ShowsBanner);
        Assert.False(update.CheckCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- helpers

    private AppInstallation Installation => new(Path.Combine(_root, "Picture Book"), Exe);

    private UpdateInstaller CreateInstaller(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        WriteFile(Installation.ExecutablePath, "old exe");
        return new UpdateInstaller(new HttpClient(new StubHandler(respond)), Installation);
    }

    private UpdateViewModel CreateViewModel(ISettingsStore settings, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var service = new UpdateService(new HttpClient(new StubHandler(respond)), new Version(0, 1, 0, 0), "win-x64");
        return new UpdateViewModel(settings, () => { }, service, CreateInstaller(respond), _root);
    }

    private static UpdateCheckResult Available(Uri package, string? sha256 = null)
    {
        var manifest = new UpdateManifest(new Version(0, 2, 0, 0), null, null, null)
        {
            Downloads = new Dictionary<string, Uri> { ["win-x64"] = package },
            Checksums = sha256 is null ? new Dictionary<Uri, string>() : new Dictionary<Uri, string> { [package] = sha256 },
        };
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, new Version(0, 1, 0, 0), manifest, null, package);
    }

    private static byte[] ZipOf(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static void WriteFile(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: true);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
