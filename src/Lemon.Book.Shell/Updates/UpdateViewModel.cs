using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.Infrastructures.Settings;

namespace Lemon.Book.Shell.Updates;

/// <summary>
/// Updates on the desktop: the update address (see <see cref="UpdateFeed"/>), checking, and installing. A new version
/// shows as a banner under the toolbar; "Update and restart" downloads, verifies and unpacks it there (with progress,
/// cancellable), hands over to the updater and exits, and the new version starts a few seconds later. When it cannot
/// be installed in place (an installer, a read-only folder, a failed attempt) the button downloads it in the browser.
/// The web edition updates itself through its service worker and shows none of this.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly UpdateService _service;
    private readonly UpdateInstaller _installer;
    private readonly Action _exit;
    private readonly string? _installFolder;
    private UpdateCheckResult? _result;
    private CancellationTokenSource? _installCancellation;

    /// <param name="exit">Ends the app, so the updater can swap its files.</param>
    public UpdateViewModel(ISettingsStore settings, Action exit)
        : this(settings, exit, new UpdateService(), new UpdateInstaller(), OperatingSystem.IsBrowser() ? null : AppContext.BaseDirectory)
    {
    }

    /// <param name="installFolder">Where <see cref="UpdateFeed.FileName"/> is looked for; null where updates are not supported.</param>
    public UpdateViewModel(ISettingsStore settings, Action exit, UpdateService service, UpdateInstaller installer, string? installFolder)
    {
        _settings = settings;
        _exit = exit;
        _service = service;
        _installer = installer;
        _installFolder = installFolder;
        _checkOnStartup = settings.Get(UpdateFeed.CheckOnStartupSetting) != bool.FalseString;
        _feedText = Resolve().Feed ?? string.Empty;
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>False in the web edition, which updates through its service worker.</summary>
    public bool IsSupported => _installFolder is not null;

    public string CurrentVersion => _service.CurrentVersion.ToString(3);

    #region the update address

    /// <summary>The address being edited in the settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedError))]
    private string _feedText;

    /// <summary>Why <see cref="FeedText"/> cannot be used, or null.</summary>
    public string? FeedError => UpdateFeed.IsValid(FeedText) ? null : LocalizationService.Instance.GetString("Update_Feed_Invalid");

    /// <summary>Where the address in force comes from, e.g. "built into this release".</summary>
    public string FeedOriginText
    {
        get
        {
            var (feed, origin) = Resolve();
            var localization = LocalizationService.Instance;
            if (UpdateFeed.Parse(feed) is null)
            {
                return localization.GetString("Update_Feed_None");
            }

            return localization.GetString(origin switch
            {
                UpdateFeedOrigin.Reader => "Update_Feed_FromReader",
                UpdateFeedOrigin.InstallFolder => "Update_Feed_FromFile",
                _ => "Update_Feed_BuiltIn",
            });
        }
    }

    /// <summary>The reader has an address of their own, which <see cref="ResetFeedCommand"/> removes.</summary>
    public bool HasReaderFeed => Resolve().Origin == UpdateFeedOrigin.Reader;

    /// <summary>Saves <see cref="FeedText"/> as the reader's own address (an empty one removes it).</summary>
    [RelayCommand]
    private void SaveFeed()
    {
        if (FeedError is not null)
        {
            return;
        }

        // The same as what would be used anyway is not an override: the release can still change it later.
        var text = FeedText.Trim();
        _settings.Set(UpdateFeed.SettingKey, null);
        if (text.Length > 0 && text != Resolve().Feed)
        {
            _settings.Set(UpdateFeed.SettingKey, text);
        }

        RefreshFeed();
        Status = LocalizationService.Instance.GetString("Update_Feed_Saved");
    }

    /// <summary>Back to the address this copy came with.</summary>
    [RelayCommand]
    private void ResetFeed()
    {
        _settings.Set(UpdateFeed.SettingKey, null);
        RefreshFeed();
    }

    private (string? Feed, UpdateFeedOrigin Origin) Resolve() =>
        _installFolder is null ? (null, UpdateFeedOrigin.BuiltIn) : UpdateFeed.Resolve(_settings, _installFolder);

    private void RefreshFeed()
    {
        FeedText = Resolve().Feed ?? string.Empty;
        OnPropertyChanged(nameof(FeedOriginText));
        OnPropertyChanged(nameof(HasReaderFeed));
    }

    #endregion

    #region checking

    [ObservableProperty]
    private bool _checkOnStartup;

    partial void OnCheckOnStartupChanged(bool value) =>
        _settings.Set(UpdateFeed.CheckOnStartupSetting, value ? null : bool.FalseString);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    /// <summary>The last check's outcome, in the settings: up to date, a new version, or why it failed.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>A newer version exists (the banner, and a dot on the settings button).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBanner))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _latestVersion = string.Empty;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText))]
    private bool _canInstall;

    /// <summary>The banner was closed with "Later" (until the next check).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBanner))]
    private bool _isDismissed;

    /// <summary>A line after an update ("updated to 0.2.0"), shown in the banner until closed; or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBanner), nameof(HasNotice))]
    private string? _notice;

    public bool HasNotice => Notice is not null;

    public bool ShowsBanner => IsSupported && ((IsUpdateAvailable && !IsDismissed) || Notice is not null);

    /// <summary>The banner's sentence about the new version.</summary>
    public string BannerText => LocalizationService.Instance.Format(
        CanInstall ? "Update_Banner_Install" : "Update_Banner_Download", LatestVersion);

    public string PrimaryButtonText => LocalizationService.Instance.GetString(CanInstall ? "Update_InstallAndRestart" : "Update_Download");

    /// <summary>Checks quietly once the book is open; only a newer version shows anything.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (IsSupported && CheckOnStartup && UpdateFeed.Parse(Resolve().Feed) is not null)
        {
            await RunCheckAsync(quiet: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunCheckAsync(quiet: false);

    private bool CanCheck() => !IsChecking && IsSupported;

    private async Task RunCheckAsync(bool quiet)
    {
        IsChecking = true;
        if (!quiet) Status = LocalizationService.Instance.GetString("Update_Checking");
        try
        {
            var result = await _service.CheckAsync(UpdateFeed.Parse(Resolve().Feed));
            Apply(result, quiet);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void Apply(UpdateCheckResult result, bool quiet)
    {
        var localization = LocalizationService.Instance;
        _result = result;
        InstallError = null;
        LatestVersion = result.Latest?.Version.ToString(3) ?? string.Empty;
        ReleaseNotes = string.IsNullOrWhiteSpace(result.Latest?.ReleaseNotes) ? null : result.Latest!.ReleaseNotes!.Trim();
        CanInstall = result.Status == UpdateCheckStatus.UpdateAvailable && _installer.CanInstall(result);

        // A failed check says nothing about whether there is a new version: the banner keeps its last state.
        if (result.Status != UpdateCheckStatus.Failed)
        {
            IsUpdateAvailable = result.Status == UpdateCheckStatus.UpdateAvailable;
            IsDismissed = false;
        }

        Status = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => localization.Format("Update_Status_Available", LatestVersion),
            UpdateCheckStatus.UpToDate => localization.Format("Update_Status_UpToDate", CurrentVersion),
            _ when quiet => string.Empty,
            _ => result.Error ?? string.Empty,
        };
        OnPropertyChanged(nameof(BannerText));
    }

    #endregion

    #region installing

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallOrDownloadCommand))]
    private bool _isInstalling;

    /// <summary>0 to 100; meaningless while <see cref="IsProgressIndeterminate"/>.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private string? _progressText;

    /// <summary>Why installing failed; the button then downloads instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallError))]
    private string? _installError;

    public bool HasInstallError => InstallError is not null;

    /// <summary>Installs in place and restarts when it can; otherwise opens the download in the browser.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallOrDownload))]
    private async Task InstallOrDownloadAsync()
    {
        if (_result is null)
        {
            return;
        }

        if (!CanInstall)
        {
            if (_result.DownloadUrl is { } url) OpenInBrowser(url);
            return;
        }

        var localization = LocalizationService.Instance;
        InstallError = null;
        IsInstalling = true;
        IsProgressIndeterminate = true;
        ProgressText = localization.GetString("Update_Downloading");
        _installCancellation = new CancellationTokenSource();
        try
        {
            var prepared = await _installer.PrepareAsync(_result, new Progress<UpdateDownloadProgress>(OnDownloadProgress), _installCancellation.Token);
            ProgressText = localization.GetString("Update_Restarting");
            _installer.Launch(prepared);
            _exit();
        }
        catch (OperationCanceledException)
        {
            ProgressText = null;
        }
        catch (UpdateInstallException ex)
        {
            InstallError = ex.Message;
            CanInstall = false; // the next click downloads by hand
            OnPropertyChanged(nameof(BannerText));
        }
        finally
        {
            IsInstalling = false;
            _installCancellation?.Dispose();
            _installCancellation = null;
        }
    }

    private bool CanInstallOrDownload() => !IsInstalling;

    /// <summary>"Later" closes the banner; while downloading it cancels.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (IsInstalling)
        {
            _installCancellation?.Cancel();
            return;
        }

        Notice = null;
        IsDismissed = true;
    }

    /// <summary>The release's page, for its notes and every download.</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        if (_result?.Latest?.PageUrl is { } page) OpenInBrowser(page);
    }

    /// <summary>
    /// Called once when the app is up: right after an update, says which version it is now (or why the update did
    /// not install), and removes what the update left behind.
    /// </summary>
    public async Task ReportLastUpdateAsync()
    {
        if (!IsSupported || await UpdateInstaller.CleanUpAfterUpdateAsync() is not { } result)
        {
            return;
        }

        Notice = LocalizationService.Instance.Format(result.Failed ? "Update_Notice_Failed" : "Update_Notice_Updated", CurrentVersion);
    }

    private void OnDownloadProgress(UpdateDownloadProgress value)
    {
        var localization = LocalizationService.Instance;
        if (value.Fraction is { } fraction)
        {
            IsProgressIndeterminate = false;
            Progress = fraction * 100;
            ProgressText = localization.Format("Update_Progress", Math.Round(Progress), FormatSize(value.Received), FormatSize(value.Total!.Value));
        }
        else
        {
            ProgressText = localization.Format("Update_Progress_Unknown", FormatSize(value.Received));
        }
    }

    #endregion

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    /// <summary>Only http(s): shell execution would also open files and custom protocols.</summary>
    private static void OpenInBrowser(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }

        var start = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("/usr/bin/open") { ArgumentList = { uri.AbsoluteUri } }
            : new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(FeedOriginText));
        OnPropertyChanged(nameof(FeedError));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }
}
