using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Lemon.Pet.Look;

namespace Lemon.Book.Shell.Sync;

/// <summary>Where the account and its sync stand, for the settings to say.</summary>
public enum SyncState
{
    /// <summary>No sync server in this build: sign-in is not offered.</summary>
    Unavailable,

    SignedOut,

    /// <summary>The sign-in page is open (desktop) or the answer is being read.</summary>
    SigningIn,

    Syncing,

    /// <summary>Signed in, and the last sync went through.</summary>
    Synced,

    /// <summary>Signed in, but the last sync did not reach the server (offline): it tries again later.</summary>
    Offline,

    /// <summary>The account's sign-in ran out: the child has to sign in again. Progress keeps saving on the device.</summary>
    NeedsSignIn,
}

/// <summary>
/// Signing in to the sync server's account and keeping the book's saved state in step with it. Without an account
/// nothing changes: the book saves on the device. Signed in, it syncs on start, a little after each change, and every
/// few minutes; changes from other devices are merged in (see <see cref="SyncMerge"/>) and <see cref="RemoteChanged"/>
/// asks the app to rebuild the book over them. Runs on the UI thread (the store is not shared across threads); only
/// the network waits are elsewhere.
/// </summary>
public sealed class SyncService
{
    public const string SessionKey = "account.session";
    internal const string PendingKey = "account.pending";

    /// <summary>The child whose progress this device syncs (<see cref="SyncChild"/>); device-only like the session.</summary>
    public const string ChildKey = "account.child";

    /// <summary>Where the web edition's sign-in page (signin.html) leaves the answer for the next start.</summary>
    internal const string CallbackKey = "account.callback";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AfterChange = TimeSpan.FromSeconds(20);

    private readonly SyncedSettingsStore _store;
    private readonly Uri? _server;
    private readonly ISignInFlow? _flow;
    private readonly HttpClient _http;
    private readonly OidcClient _oidc;
    private readonly Func<DateTime> _now;
    private readonly string _deviceName;
    private readonly string _platform;
    private CancellationTokenSource? _signIn;
    private Task? _running;
    private bool _again;
    private DateTime? _dueAt;

    public SyncService(SyncedSettingsStore store, Uri? server, ISignInFlow? flow, HttpClient? http = null, Func<DateTime>? now = null)
    {
        _store = store;
        _server = server;
        _flow = flow;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _oidc = new OidcClient(_http);
        _now = now ?? (() => DateTime.UtcNow);
        _deviceName = OperatingSystem.IsBrowser() ? "浏览器" : SafeMachineName();
        _platform = OperatingSystem.IsBrowser() ? "browser" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other";
        Session = LoadSession();
        Child = Read(_store.Inner.Get(ChildKey), SyncJson.Default.SyncChild);
        State = !IsAvailable ? SyncState.Unavailable : Session is null ? SyncState.SignedOut : SyncState.Synced;
        _store.LocalChanged += (_, _) => Schedule(AfterChange);
    }

    /// <summary>A sync server is configured and this head can sign in.</summary>
    public bool IsAvailable => _server is not null && _flow is not null;

    /// <summary>The sync server (the hub, which keeps the child's pet too), or null when there is none.</summary>
    public Uri? Server => _server;

    public AccountSession? Session { get; private set; }

    public bool IsSignedIn => Session is not null;

    /// <summary>
    /// The parent's child this device syncs, or null: the account's own progress (the child signed in themselves, or
    /// no child is chosen yet).
    /// </summary>
    public SyncChild? Child { get; private set; }

    public SyncState State { get; private set; }

    public DateTime? LastSyncedAt { get; private set; }

    /// <summary>State, account or last sync changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Values from another device were taken into the store: the book should be built again over them.</summary>
    public event EventHandler? RemoteChanged;

    /// <summary>
    /// At app start: finishes a sign-in whose answer arrived while the app was closed (the web edition), then syncs.
    /// Then call <see cref="Tick"/> now and then (the app's timer).
    /// </summary>
    public async Task StartAsync()
    {
        if (!IsAvailable) return;
        if (_store.Inner.Get(CallbackKey) is { } callback)
        {
            _store.Inner.Set(CallbackKey, null);
            await CompleteSignInAsync(callback, CancellationToken.None);
            return;
        }

        if (IsSignedIn) await SyncNowAsync();
    }

    /// <summary>Called by the app's timer: syncs when one is due.</summary>
    public void Tick()
    {
        if (!IsSignedIn || State == SyncState.NeedsSignIn) return;
        var now = _now();
        if (_dueAt is { } due && now >= due || LastSyncedAt is null || now - LastSyncedAt >= Interval)
        {
            _ = SyncNowAsync();
        }
    }

    private void Schedule(TimeSpan after)
    {
        var at = _now() + after;
        if (_dueAt is null || at < _dueAt) _dueAt = at;
    }

    public async Task SignInAsync()
    {
        if (!IsAvailable) return;
        _signIn?.Cancel();
        var cancel = _signIn = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        SetState(SyncState.SigningIn);
        try
        {
            var client = await GetClientAsync(cancel.Token);
            var (pending, url) = await _oidc.BeginAsync(client, _flow!.RedirectUri, _now(), cancel.Token);
            _store.Inner.Set(PendingKey, JsonSerializer.Serialize(pending, SyncJson.Default.PendingSignIn));
            var answer = await _flow.SignInAsync(url, cancel.Token);
            if (answer is not null) await CompleteSignInAsync(answer, cancel.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or SignInException or OperationCanceledException or JsonException)
        {
            Log(ex);
            SetState(IsSignedIn ? SyncState.Offline : SyncState.SignedOut);
        }
    }

    /// <summary>Stops a sign-in in progress (the reader closed the browser without answering).</summary>
    public void CancelSignIn()
    {
        _signIn?.Cancel();
        if (State == SyncState.SigningIn) SetState(IsSignedIn ? SyncState.Synced : SyncState.SignedOut);
    }

    /// <summary>
    /// Forgets the account on this device. The book keeps everything it has (it is the child's progress, and still
    /// in the account too); it just stops syncing.
    /// </summary>
    public void SignOut()
    {
        _signIn?.Cancel();
        Session = null;
        _store.Inner.Set(SessionKey, null);
        SaveChild(null);
        _store.Inner.Set(PendingKey, null);
        LastSyncedAt = null;
        SetState(IsAvailable ? SyncState.SignedOut : SyncState.Unavailable);
    }

    private async Task CompleteSignInAsync(string answer, CancellationToken cancellationToken)
    {
        var pending = Read(_store.Inner.Get(PendingKey), SyncJson.Default.PendingSignIn);
        _store.Inner.Set(PendingKey, null);
        if (pending is null)
        {
            SetState(IsSignedIn ? SyncState.Synced : SyncState.SignedOut);
            return;
        }

        SetState(SyncState.SigningIn);
        try
        {
            var session = await _oidc.CompleteAsync(pending, answer, _now(), cancellationToken);
            // Signing in again to the same account (its sign-in ran out) carries on where it was.
            var firstTime = Session?.UserId is null || Session.UserId != session.UserId;
            SaveSession(session);
            if (firstTime)
            {
                SaveChild(null);
                // Everything on this device goes to the account, merged with what it has from other devices.
                _store.ResetForAccount();
            }

            await SyncNowAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or SignInException or OperationCanceledException or JsonException)
        {
            Log(ex);
            SetState(IsSignedIn ? SyncState.Offline : SyncState.SignedOut);
        }
    }

    /// <summary>The signed-in parent's children, as the server has them.</summary>
    public async Task<IReadOnlyList<SyncChild>> GetChildrenAsync(CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(_server!, "api/app/family/children"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync());
        using var response = await _http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(body, SyncJson.Default.ListSyncChild) ?? [];
    }

    /// <summary>Adds a child to the parent's account (the server keeps only the name the parent gives).</summary>
    public async Task<SyncChild> AddChildAsync(string name, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_server!, "api/app/family/children"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new NewChildRequest(name.Trim()), SyncJson.Default.NewChildRequest), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync());
        using var response = await _http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(body, SyncJson.Default.SyncChild)
               ?? throw new JsonException("The server did not return the new child.");
    }

    /// <summary>
    /// The chosen child's pet on the server (the account's own without a child), or null when they have none. The pet
    /// grows on the server from the events this book sends; it is chosen and looked after in the pet app (柠檬宠物).
    /// </summary>
    public async Task<PetInfo?> GetPetAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn) return null;
        var query = Child is { } child ? $"?child={child.Id}" : string.Empty;
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(_server!, $"api/app/pet{query}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync());
        using var response = await _http.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(body, SyncJson.Default.PetInfo);
    }

    /// <summary>
    /// Syncs this device as <paramref name="child"/> from now on. Like signing in, what the book has here goes into
    /// the child's progress, merged with what the child has from other devices.
    /// </summary>
    public Task ChooseChildAsync(SyncChild child)
    {
        if (!IsSignedIn || Child?.Id == child.Id) return Task.CompletedTask;
        SaveChild(child);
        _store.ResetForAccount();
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return SyncNowAsync();
    }

    /// <summary>Syncs now (one at a time: asked again while one runs, it runs once more after).</summary>
    public Task SyncNowAsync()
    {
        if (!IsSignedIn) return Task.CompletedTask;
        if (_running is { IsCompleted: false })
        {
            _again = true;
            return _running;
        }

        return _running = RunAsync();
    }

    private async Task RunAsync()
    {
        do
        {
            _again = false;
            _dueAt = null;
            SetState(SyncState.Syncing);
            try
            {
                var changed = false;
                // A conflict merged here goes back at once; a few rounds settle any race.
                for (var round = 0; round < 3; round++)
                {
                    var (roundChanged, more) = await SyncRoundAsync();
                    changed |= roundChanged;
                    if (!more) break;
                }

                LastSyncedAt = _now();
                SetState(SyncState.Synced);
                if (changed) RemoteChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (SignInException ex) when (ex.NeedsSignIn)
            {
                Log(ex);
                SetState(SyncState.NeedsSignIn);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or SignInException or OperationCanceledException or JsonException)
            {
                Log(ex);
                SetState(SyncState.Offline);
                Schedule(Interval);
                return;
            }
        }
        while (_again);
    }

    /// <summary>One request: what changed here and the events go up, what changed elsewhere comes down.</summary>
    /// <returns>Whether values here changed, and whether merged values wait to go back up.</returns>
    private async Task<(bool Changed, bool More)> SyncRoundAsync()
    {
        var token = await AccessTokenAsync();
        var changes = _store.PendingChanges();
        var events = _store.Events.Pending.Take(500).ToList();
        var request = new SyncRequest
        {
            DeviceId = _store.DeviceId,
            DeviceName = _deviceName,
            Platform = _platform,
            AppVersion = AppVersion,
            Since = _store.Cursor,
            Changes = changes,
            Events = events,
        };

        var child = Child;
        var query = child is null ? string.Empty : $"?child={child.Id}";
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_server!, $"api/app/learning/{SyncServer.App}/sync{query}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(request, SyncJson.Default.SyncRequest), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(message);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The access token was refused before it expired (the service restarted with new keys): refresh next time.
            SaveSession(Session! with { ExpiresAt = DateTime.MinValue });
            throw new HttpRequestException("Sync refused the access token.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound && child is not null)
        {
            // The parent removed the child: what the book has goes to the account, as before a child was chosen.
            SaveChild(null);
            _store.ResetForAccount();
            throw new HttpRequestException("The chosen child is no longer in the account.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Another device of this account synced at the same moment: try again shortly.
            return (false, true);
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        if (Child?.Id != child?.Id)
        {
            // Another child was chosen while this went: its versions are not theirs. Send again as the new child.
            return (false, true);
        }

        var result = JsonSerializer.Deserialize(body, SyncJson.Default.SyncResponse) ?? new SyncResponse();

        var sent = changes.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        foreach (var accepted in result.Accepted)
        {
            _store.Accepted(accepted.Key, sent.GetValueOrDefault(accepted.Key), accepted.Version);
        }

        _store.Events.Sent(events.Select(e => e.Id));

        var changed = false;
        var more = false;
        foreach (var record in result.Conflicts.Concat(result.Changes))
        {
            var (recordChanged, needsSend) = Take(record);
            changed |= recordChanged;
            more |= needsSend;
        }

        _store.Cursor = Math.Max(_store.Cursor, result.Cursor);
        return (changed, more);
    }

    /// <summary>Takes a server value: as it is, or merged with this device's when that changed too.</summary>
    private (bool Changed, bool NeedsSend) Take(SyncRecord record)
    {
        if (SyncedSettingsStore.IsDeviceOnly(record.Key)) return (false, false);
        var remote = record.Deleted ? null : record.Value;
        var journal = _store.JournalOf(record.Key);
        if (journal is not { Dirty: true })
        {
            return (_store.ApplyFromServer(record.Key, remote, record.Version), false);
        }

        var local = _store.Get(record.Key);
        var merged = SyncMerge.Merge(record.Key, (local, journal.ChangedAt), (remote, record.ChangedAt));
        var dirty = merged != remote;
        return (_store.ApplyFromServer(record.Key, merged, record.Version, dirty), dirty);
    }

    /// <summary>
    /// A current access token, refreshed when due, for the pet (Lemon.Pet.Core) on this sign-in; null when signed out
    /// or the sign-in ran out. Network failures still throw: the pet says it is offline.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        if (!IsSignedIn) return null;
        try
        {
            return await AccessTokenAsync();
        }
        catch (SignInException)
        {
            return null;
        }
    }

    private async Task<string> AccessTokenAsync()
    {
        var session = Session ?? throw new SignInException("Not signed in.", needsSignIn: true);
        if (session.ExpiresAt - _now() > TimeSpan.FromMinutes(1)) return session.AccessToken;
        session = await _oidc.RefreshAsync(session, _now(), CancellationToken.None);
        SaveSession(session);
        return session.AccessToken;
    }

    private async Task<SyncClientInfo> GetClientAsync(CancellationToken cancellationToken)
    {
        var body = await _http.GetStringAsync(new Uri(_server!, $"api/app/learning/{SyncServer.App}/client"), cancellationToken);
        return JsonSerializer.Deserialize(body, SyncJson.Default.SyncClientInfo)
               ?? throw new SignInException("The sync server did not say how to sign in.");
    }

    private void SaveSession(AccountSession session)
    {
        Session = session;
        _store.Inner.Set(SessionKey, JsonSerializer.Serialize(session, SyncJson.Default.AccountSession));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveChild(SyncChild? child)
    {
        Child = child;
        _store.Inner.Set(ChildKey, child is null ? null : JsonSerializer.Serialize(child, SyncJson.Default.SyncChild));
    }

    private AccountSession? LoadSession() => Read(_store.Inner.Get(SessionKey), SyncJson.Default.AccountSession);

    private void SetState(SyncState state)
    {
        State = state;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static T? Read<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? AppVersion =>
        BookShell.AppAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return "desktop";
        }
    }

    private static void Log(Exception ex) => System.Diagnostics.Trace.WriteLine($"Sync: {ex.GetType().Name}: {ex.Message}");
}
