namespace Lemon.Hub.Client;

/// <summary>
/// The account an app signs in to the hub with, on this device: signing in through the head's
/// <see cref="ISignInFlow"/>, keeping the session in the app's <see cref="ISessionStore"/>, and handing out a current
/// access token, refreshed when due. Safe to call from any thread; refreshes that overlap share one request, so a
/// refresh token that the service rotates is never spent twice.
/// </summary>
public sealed class HubAccount
{
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(1);

    private readonly ISessionStore _store;
    private readonly ISignInFlow? _flow;
    private readonly OidcClient _oidc;
    private readonly Func<DateTime> _now;
    private readonly Lock _lock = new();
    private AccountSession? _session;
    private Task<AccountSession>? _refreshing;

    /// <param name="store">Where the session is kept (the app's choice: see <see cref="ISessionStore"/>).</param>
    /// <param name="flow">How this head signs in, or null when it cannot (then only a kept session is used).</param>
    /// <param name="http">For the sign-in service; the app owns it.</param>
    /// <param name="now">The clock, UTC (tests).</param>
    public HubAccount(ISessionStore store, ISignInFlow? flow, HttpClient http, Func<DateTime>? now = null)
    {
        _store = store;
        _flow = flow;
        _oidc = new OidcClient(http);
        _now = now ?? (() => DateTime.UtcNow);
        _session = store.LoadSession();
    }

    /// <summary>The OpenID Connect client this account signs in with (asking the hub for its client, say).</summary>
    public OidcClient Oidc => _oidc;

    public AccountSession? Session
    {
        get
        {
            lock (_lock) return _session;
        }
    }

    public bool IsSignedIn => Session is not null;

    /// <summary>This head can sign in (it has a flow).</summary>
    public bool CanSignIn => _flow is not null;

    /// <summary>A sign-in was begun and its answer has not been read yet (the web edition, after the redirect).</summary>
    public bool HasPendingSignIn => _store.LoadPending() is not null;

    /// <summary>The session was saved, refreshed or forgotten. Raised on the thread that changed it.</summary>
    public event EventHandler? SessionChanged;

    /// <summary>
    /// Signs in as <paramref name="client"/> says: shows the sign-in page and, when the answer comes back here (the
    /// desktop), trades it for tokens and keeps the session. Null when the answer comes to a new start of the app
    /// instead (the web edition): pass it to <see cref="CompleteAsync"/> then.
    /// </summary>
    /// <exception cref="SignInException">Refused, or the answer did not match.</exception>
    /// <exception cref="HttpRequestException">The sign-in service could not be reached.</exception>
    public async Task<AccountSession?> SignInAsync(SyncClientInfo client, CancellationToken cancellationToken)
    {
        var flow = _flow ?? throw new InvalidOperationException("This head has no sign-in flow.");
        var (pending, url) = await _oidc.BeginAsync(client, flow.RedirectUri, _now(), cancellationToken);
        _store.SavePending(pending);
        var answer = await flow.SignInAsync(url, cancellationToken);
        return answer is null ? null : await CompleteAsync(answer, cancellationToken);
    }

    /// <summary>
    /// Finishes the pending sign-in with its answer (<c>?code=…&amp;state=…</c>) and keeps the session. Null when no
    /// sign-in was pending (an answer read twice, or after signing out). The pending sign-in is spent either way.
    /// </summary>
    public async Task<AccountSession?> CompleteAsync(string callbackQuery, CancellationToken cancellationToken)
    {
        var pending = _store.LoadPending();
        _store.SavePending(null);
        if (pending is null) return null;
        var session = await _oidc.CompleteAsync(pending, callbackQuery, _now(), cancellationToken);
        Save(session);
        return session;
    }

    /// <summary>
    /// A current access token, refreshed first when it is about to run out.
    /// </summary>
    /// <exception cref="SignInException">
    /// Not signed in, or the refresh was refused (<see cref="SignInException.NeedsSignIn"/>): the user signs in again.
    /// </exception>
    /// <exception cref="HttpRequestException">Offline: the token could not be refreshed now.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        AccountSession session;
        TaskCompletionSource<AccountSession>? mine = null;
        Task<AccountSession> refreshing;
        lock (_lock)
        {
            session = _session ?? throw new SignInException("Not signed in.", needsSignIn: true);
            if (session.ExpiresAt - _now() > RefreshAhead) return session.AccessToken;
            if (_refreshing is null)
            {
                mine = new TaskCompletionSource<AccountSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                _refreshing = mine.Task;
            }

            refreshing = _refreshing;
        }

        if (mine is not null) _ = RefreshAsync(session, mine);

        // The refresh is shared, so one caller giving up does not cancel it for the others.
        return (await refreshing.WaitAsync(cancellationToken)).AccessToken;
    }

    /// <summary>The access token was refused before it expired (the service restarted with new keys): refresh next time.</summary>
    public void Expire()
    {
        lock (_lock)
        {
            if (_session is null) return;
            _session = _session with { ExpiresAt = DateTime.MinValue };
            _store.SaveSession(_session);
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the session and any sign-in under way, on this device.</summary>
    public void SignOut()
    {
        lock (_lock)
        {
            _session = null;
            _refreshing = null;
            _store.SaveSession(null);
            _store.SavePending(null);
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshAsync(AccountSession session, TaskCompletionSource<AccountSession> result)
    {
        try
        {
            var refreshed = await _oidc.RefreshAsync(session, _now(), CancellationToken.None);
            bool kept;
            lock (_lock)
            {
                if (_refreshing == result.Task) _refreshing = null;
                // Signed out (or in again) while the refresh went: that session is not this one any more.
                kept = ReferenceEquals(_session, session);
                if (kept)
                {
                    _session = refreshed;
                    _store.SaveSession(refreshed);
                }
            }

            if (kept) SessionChanged?.Invoke(this, EventArgs.Empty);
            result.SetResult(refreshed);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (_refreshing == result.Task) _refreshing = null;
            }

            result.SetException(ex);
        }
    }

    private void Save(AccountSession session)
    {
        lock (_lock)
        {
            _session = session;
            _refreshing = null;
            _store.SaveSession(session);
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }
}
