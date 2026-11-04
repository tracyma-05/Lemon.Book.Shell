using Xunit;

namespace Lemon.Hub.Client.Tests;

public sealed class SessionStoreTests
{
    private static readonly AccountSession Session = new()
    {
        AccessToken = "access",
        RefreshToken = "refresh",
        ExpiresAt = new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc),
        UserName = "Ada",
        UserId = "user-1",
        Authority = "https://sso.example.test",
        ClientId = "test_app",
    };

    [Fact]
    public void KeepsTheSessionAndPendingUnderTheirKeys()
    {
        var values = new Dictionary<string, string?>();
        var store = new KeyValueSessionStore(k => values.GetValueOrDefault(k), (k, v) => values[k] = v);

        store.SaveSession(Session);
        store.SavePending(new PendingSignIn { State = "s", Verifier = "v" });

        Assert.Equal(Session, store.LoadSession());
        Assert.Equal("s", store.LoadPending()?.State);
        Assert.Contains("\"refreshToken\":\"refresh\"", values[KeyValueSessionStore.DefaultSessionKey]);
        Assert.Contains("\"verifier\":\"v\"", values[KeyValueSessionStore.DefaultPendingKey]);

        store.SaveSession(null);
        store.SavePending(null);
        Assert.Null(values[KeyValueSessionStore.DefaultSessionKey]);
        Assert.Null(store.LoadSession());
        Assert.Null(store.LoadPending());
    }

    [Fact]
    public void ReadsASessionSavedByTheShellBeforeTheMove()
    {
        // As Lemon.Book.Shell 0.1.x wrote it (its SyncJson, camelCase): a reader stays signed in after the update.
        const string saved = "{\"accessToken\":\"access\",\"refreshToken\":\"refresh\",\"expiresAt\":\"2026-11-04T10:00:00Z\","
                             + "\"userName\":\"Ada\",\"userId\":\"user-1\",\"authority\":\"https://sso.example.test\",\"clientId\":\"test_app\"}";
        Assert.Equal(Session, SessionJson.ReadSession(saved));
    }

    [Fact]
    public void UnreadableTextReadsAsSignedOut()
    {
        Assert.Null(SessionJson.ReadSession("{not json"));
        Assert.Null(SessionJson.ReadSession(""));
        Assert.Null(SessionJson.ReadPending(null));
    }

    [Fact]
    public void AnAppCanProtectWhatIsKept()
    {
        // The way an app encrypts the session (here a stand-in that reverses the text): wrap get and set.
        var values = new Dictionary<string, string?>();
        static string? Flip(string? text) => text is null ? null : new string(text.Reverse().ToArray());
        var store = new KeyValueSessionStore(k => Flip(values.GetValueOrDefault(k)), (k, v) => values[k] = Flip(v));

        store.SaveSession(Session);

        Assert.DoesNotContain("refresh", values[KeyValueSessionStore.DefaultSessionKey]!.Replace("hserfer", "", StringComparison.Ordinal));
        Assert.StartsWith("}", values[KeyValueSessionStore.DefaultSessionKey], StringComparison.Ordinal);
        Assert.Equal(Session, store.LoadSession());
    }
}
