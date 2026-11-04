using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.Sync;
using Lemon.Hub.Client;

namespace Lemon.Book.Shell.ViewModels;

/// <summary>
/// 账号与同步 in the settings: sign in so progress, the 生词本 and the rest follow the child to another device; or
/// stay signed out, and everything stays on this one.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly SyncService _sync;

    public AccountViewModel(SyncService sync)
    {
        _sync = sync;
        _sync.StatusChanged += (_, _) => Refresh();
        LocalizationService.Instance.PropertyChanged += (_, _) => Refresh();
    }

    /// <summary>This build has a sync server and this head can sign in.</summary>
    public bool IsAvailable => _sync.IsAvailable;

    public bool IsSignedIn => _sync.IsSignedIn;

    public bool IsSignedOut => !_sync.IsSignedIn;

    public bool IsSigningIn => _sync.State == SyncState.SigningIn;

    /// <summary>The account's sign-in ran out: sign in again (the button says so).</summary>
    public bool NeedsSignIn => _sync.State == SyncState.NeedsSignIn;

    public bool CanSync => _sync.IsSignedIn && _sync.State is not (SyncState.Syncing or SyncState.SigningIn);

    /// <summary>"已登录：小明" or "未登录：读到哪儿只保存在这台设备上。"</summary>
    public string AccountText
    {
        get
        {
            var strings = LocalizationService.Instance;
            return _sync.Session is { } session
                ? strings.Format("Account_SignedInAs", session.UserName ?? strings.GetString("Account_Someone"))
                : strings.GetString("Account_SignedOut");
        }
    }

    /// <summary>Whose progress this device syncs: "同步的是：小柠檬 的进度", or that no child is chosen yet.</summary>
    public string ChildText =>
        _sync.Child is { } child
            ? LocalizationService.Instance.Format("Account_ChildIs", child.Name)
            : LocalizationService.Instance.GetString("Account_NoChild");

    /// <summary>How the last sync went, e.g. "已同步 · 10:32".</summary>
    public string StatusText
    {
        get
        {
            var strings = LocalizationService.Instance;
            return _sync.State switch
            {
                SyncState.SigningIn => strings.GetString("Account_SigningIn"),
                SyncState.Syncing => strings.GetString("Account_Syncing"),
                SyncState.Synced when _sync.LastSyncedAt is { } at =>
                    strings.Format("Account_SyncedAt", at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)),
                SyncState.Offline => strings.GetString("Account_Offline"),
                SyncState.NeedsSignIn => strings.GetString("Account_NeedsSignIn"),
                _ => string.Empty,
            };
        }
    }

    [RelayCommand]
    private Task SignIn() => _sync.SignInAsync();

    [RelayCommand]
    private void CancelSignIn() => _sync.CancelSignIn();

    [RelayCommand]
    private Task SyncNow() => _sync.SyncNowAsync();

    /// <summary>Sign-out is asked once more: the child should not do it by accident.</summary>
    [ObservableProperty]
    private bool _isConfirmingSignOut;

    [RelayCommand]
    private void AskSignOut() => IsConfirmingSignOut = true;

    [RelayCommand]
    private void ConfirmSignOut()
    {
        IsConfirmingSignOut = false;
        _sync.SignOut();
    }

    [RelayCommand]
    private void KeepSignedIn() => IsConfirmingSignOut = false;

    /// <summary>The parent is choosing which child this device syncs.</summary>
    [ObservableProperty]
    private bool _isChoosingChild;

    /// <summary>The account's children, when choosing.</summary>
    public ObservableCollection<SyncChild> Children { get; } = [];

    /// <summary>A new child's name, typed by the parent.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddChildCommand))]
    private string _newChildName = string.Empty;

    /// <summary>Why the children could not be listed or added, or that there are none yet.</summary>
    [ObservableProperty]
    private string _childrenNote = string.Empty;

    [RelayCommand]
    private async Task ChooseChild()
    {
        IsChoosingChild = true;
        Children.Clear();
        ChildrenNote = string.Empty;
        try
        {
            foreach (var child in await _sync.GetChildrenAsync()) Children.Add(child);
            if (Children.Count == 0) ChildrenNote = LocalizationService.Instance.GetString("Account_NoChildren");
        }
        catch (Exception ex) when (ex is HttpRequestException or SignInException or OperationCanceledException or JsonException)
        {
            ChildrenNote = LocalizationService.Instance.GetString("Account_ChildrenFailed");
        }
    }

    [RelayCommand]
    private Task PickChild(SyncChild child)
    {
        IsChoosingChild = false;
        return _sync.ChooseChildAsync(child);
    }

    private bool CanAddChild() => !string.IsNullOrWhiteSpace(NewChildName);

    [RelayCommand(CanExecute = nameof(CanAddChild))]
    private async Task AddChild()
    {
        try
        {
            var child = await _sync.AddChildAsync(NewChildName);
            NewChildName = string.Empty;
            await PickChild(child);
        }
        catch (Exception ex) when (ex is HttpRequestException or SignInException or OperationCanceledException or JsonException)
        {
            ChildrenNote = LocalizationService.Instance.GetString("Account_ChildrenFailed");
        }
    }

    [RelayCommand]
    private void CloseChildren() => IsChoosingChild = false;

    private void Refresh()
    {
        if (!_sync.IsSignedIn) IsChoosingChild = false;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(IsSigningIn));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(ChildText));
        OnPropertyChanged(nameof(StatusText));
    }
}
