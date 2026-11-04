using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Lemon.Hub.Client.Tests;

public sealed class HubAccountTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SignsInWithPkceAndKeepsTheSession()
    {
        var service = new FakeSignInService();
        var store = new MemorySessionStore();
        var flow = new FakeFlow(service);
        var account = new HubAccount(store, flow, new HttpClient(service), () => Now);
        var changed = 0;
        account.SessionChanged += (_, _) => changed++;

        var session = await account.SignInAsync(FakeSignInService.Client, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal("access-1", session.AccessToken);
        Assert.Equal("refresh-1", session.RefreshToken);
        Assert.Equal("Ada", session.UserName);
        Assert.Equal("user-1", session.UserId);
        Assert.Equal(Now.AddHours(1), session.ExpiresAt);
        Assert.Equal(session, store.LoadSession());
        Assert.Null(store.LoadPending());
        Assert.Equal(1, changed);

        // The authorize URL carries the S256 challenge of the verifier the token request sent.
        var query = OidcClient.ParseQuery(flow.AuthorizeUrl!.Query);
        Assert.Equal(FakeSignInService.ClientId, query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(flow.RedirectUri.ToString(), query["redirect_uri"]);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(service.LastCodeVerifier!)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(challenge, query["code_challenge"]);
    }

    [Fact]
    public async Task TheWebAnswerIsReadOnTheNextStart()
    {
        var service = new FakeSignInService();
        var store = new MemorySessionStore();
        var flow = new FakeFlow(service, answerLater: true);

        var before = new HubAccount(store, flow, new HttpClient(service), () => Now);
        Assert.Null(await before.SignInAsync(FakeSignInService.Client, TestContext.Current.CancellationToken));
        Assert.True(before.HasPendingSignIn);

        // The page went away and came back: a new account over the same store finishes it.
        var after = new HubAccount(store, flow, new HttpClient(service), () => Now);
        var session = await after.CompleteAsync(flow.Answer!, TestContext.Current.CancellationToken);
        Assert.Equal("access-1", session?.AccessToken);
        Assert.False(after.HasPendingSignIn);

        // Read twice (a reload of the callback): nothing is pending any more.
        Assert.Null(await after.CompleteAsync(flow.Answer!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAnswerForAnotherRequestIsRefused()
    {
        var service = new FakeSignInService();
        var store = new MemorySessionStore();
        var account = new HubAccount(store, new FakeFlow(service, wrongState: "forged"), new HttpClient(service), () => Now);

        await Assert.ThrowsAsync<SignInException>(() => account.SignInAsync(FakeSignInService.Client, TestContext.Current.CancellationToken));
        Assert.False(account.IsSignedIn);
        Assert.Null(store.LoadPending());
    }

    [Fact]
    public async Task RefreshesWhenDueAndOverlappingCallersShareOneRefresh()
    {
        var service = new FakeSignInService { HoldRefresh = new TaskCompletionSource() };
        var store = new MemorySessionStore();
        store.SaveSession(ExpiredSession());
        var account = new HubAccount(store, flow: null, new HttpClient(service), () => Now);

        var first = account.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        var second = account.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        service.HoldRefresh.SetResult();

        Assert.Equal("access-1", await first);
        Assert.Equal("access-1", await second);
        Assert.Equal(1, service.RefreshRequests);
        Assert.Equal("refresh-1", store.LoadSession()?.RefreshToken);
        Assert.Equal("Ada", store.LoadSession()?.UserName);

        // Now current: no request at all.
        Assert.Equal("access-1", await account.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, service.RefreshRequests);
    }

    [Fact]
    public async Task ARefusedRefreshNeedsSignIn()
    {
        var service = new FakeSignInService { RefuseRefresh = true };
        var store = new MemorySessionStore();
        store.SaveSession(ExpiredSession());
        var account = new HubAccount(store, flow: null, new HttpClient(service), () => Now);

        var ex = await Assert.ThrowsAsync<SignInException>(() => account.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.True(ex.NeedsSignIn);

        // The next call asks again rather than keeping the failed refresh.
        await Assert.ThrowsAsync<SignInException>(() => account.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, service.RefreshRequests);
    }

    [Fact]
    public async Task SigningOutWhileARefreshGoesKeepsTheAccountSignedOut()
    {
        var service = new FakeSignInService { HoldRefresh = new TaskCompletionSource() };
        var store = new MemorySessionStore();
        store.SaveSession(ExpiredSession());
        var account = new HubAccount(store, flow: null, new HttpClient(service), () => Now);

        var token = account.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        account.SignOut();
        service.HoldRefresh.SetResult();
        await token;

        Assert.False(account.IsSignedIn);
        Assert.Null(store.LoadSession());
    }

    [Fact]
    public async Task NotSignedInNeedsSignIn()
    {
        var account = new HubAccount(new MemorySessionStore(), flow: null, new HttpClient(new FakeSignInService()));
        var ex = await Assert.ThrowsAsync<SignInException>(() => account.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.True(ex.NeedsSignIn);
        Assert.False(account.CanSignIn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.SignInAsync(FakeSignInService.Client, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExpireMakesTheNextCallRefresh()
    {
        var store = new MemorySessionStore();
        store.SaveSession(ExpiredSession() with { ExpiresAt = Now.AddHours(1) });
        var account = new HubAccount(store, flow: null, new HttpClient(new FakeSignInService()), () => Now);

        account.Expire();

        Assert.Equal(DateTime.MinValue, store.LoadSession()?.ExpiresAt);
    }

    [Fact]
    public async Task AsksTheHubHowToSignIn()
    {
        var oidc = new OidcClient(new HttpClient(new FakeSignInService()));
        var address = OidcClient.ClientInfoAddress(new Uri("https://hub.example.test/"), "test-app");

        Assert.Equal("https://hub.example.test/api/app/learning/test-app/client", address.ToString());
        Assert.Equal(FakeSignInService.Client, await oidc.GetClientInfoAsync(address, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            oidc.GetClientInfoAsync(OidcClient.ClientInfoAddress(new Uri("https://hub.example.test/"), "unknown"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadsTheIdTokenAndQueries()
    {
        var claims = OidcClient.ReadIdToken(FakeSignInService.IdToken("user-1", "Ada"));
        Assert.Equal("user-1", claims?.Sub);
        Assert.Equal("Ada", claims?.Name);
        Assert.Null(OidcClient.ReadIdToken("not-a-token"));
        Assert.Null(OidcClient.ReadIdToken("a.!!!.c"));

        var query = OidcClient.ParseQuery("?code=a%2Bb&state=x+y&empty");
        Assert.Equal("a+b", query["code"]);
        Assert.Equal("x y", query["state"]);
        Assert.Equal(string.Empty, query["empty"]);
    }

    private static AccountSession ExpiredSession() => new()
    {
        AccessToken = "old",
        RefreshToken = "refresh-0",
        ExpiresAt = Now.AddSeconds(30),
        UserName = "Ada",
        UserId = "user-1",
        Authority = FakeSignInService.Authority,
        ClientId = FakeSignInService.ClientId,
    };
}
