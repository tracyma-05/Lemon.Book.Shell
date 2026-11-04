namespace Lemon.Hub.Client;

/// <summary>
/// Where the app keeps the signed-in session (its refresh token) and a sign-in under way. The app chooses: a plain
/// settings file (<see cref="KeyValueSessionStore"/>), memory (<see cref="MemorySessionStore"/>), or its own, e.g.
/// one that encrypts the session for the current user. The web edition's pending sign-in must survive a reload of
/// the page, since the answer comes to a new start of the app.
/// </summary>
public interface ISessionStore
{
    AccountSession? LoadSession();

    /// <summary>Keeps <paramref name="session"/>, or forgets it when null (signed out).</summary>
    void SaveSession(AccountSession? session);

    PendingSignIn? LoadPending();

    void SavePending(PendingSignIn? pending);
}

/// <summary>
/// Keeps the session and the pending sign-in as JSON text under two keys of any string store (a settings file,
/// <c>localStorage</c>). To protect it, wrap <paramref name="get"/> and <paramref name="set"/> (decrypt and encrypt the
/// text); <see cref="SessionJson"/> is the format.
/// </summary>
public sealed class KeyValueSessionStore(
    Func<string, string?> get,
    Action<string, string?> set,
    string sessionKey = KeyValueSessionStore.DefaultSessionKey,
    string pendingKey = KeyValueSessionStore.DefaultPendingKey) : ISessionStore
{
    public const string DefaultSessionKey = "account.session";
    public const string DefaultPendingKey = "account.pending";

    public AccountSession? LoadSession() => SessionJson.ReadSession(get(sessionKey));

    public void SaveSession(AccountSession? session) => set(sessionKey, session is null ? null : SessionJson.Serialize(session));

    public PendingSignIn? LoadPending() => SessionJson.ReadPending(get(pendingKey));

    public void SavePending(PendingSignIn? pending) => set(pendingKey, pending is null ? null : SessionJson.Serialize(pending));
}

/// <summary>Keeps nothing past the process: tests, or an app that signs in each time it starts.</summary>
public sealed class MemorySessionStore : ISessionStore
{
    private readonly Lock _lock = new();
    private AccountSession? _session;
    private PendingSignIn? _pending;

    public AccountSession? LoadSession()
    {
        lock (_lock) return _session;
    }

    public void SaveSession(AccountSession? session)
    {
        lock (_lock) _session = session;
    }

    public PendingSignIn? LoadPending()
    {
        lock (_lock) return _pending;
    }

    public void SavePending(PendingSignIn? pending)
    {
        lock (_lock) _pending = pending;
    }
}
