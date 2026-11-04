using System.Net;
using System.Text;
using System.Text.Json;

namespace Lemon.Hub.Client.Tests;

/// <summary>
/// A sign-in service and hub in one handler: discovery, the token endpoint (codes and refresh tokens it handed out),
/// and the hub's client endpoint. Counts the token requests; <see cref="HoldRefresh"/> keeps refreshes waiting.
/// </summary>
internal sealed class FakeSignInService : HttpMessageHandler
{
    public const string Authority = "https://sso.example.test";
    public const string ClientId = "test_app";

    private int _tokens;

    public static SyncClientInfo Client { get; } = new(Authority, ClientId, "openid offline_access");

    /// <summary>The last code the authorize page "issued" (a fake browser fills it in).</summary>
    public string? IssuedCode { get; set; }

    public string? LastCodeVerifier { get; private set; }

    public int RefreshRequests { get; private set; }

    /// <summary>When set, a refresh waits for it before answering.</summary>
    public TaskCompletionSource? HoldRefresh { get; set; }

    /// <summary>The refresh token was revoked: refreshes answer invalid_grant.</summary>
    public bool RefuseRefresh { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/openid-configuration")
        {
            return Json(new Dictionary<string, string>
            {
                ["authorization_endpoint"] = Authority + "/connect/authorize",
                ["token_endpoint"] = Authority + "/connect/token",
            });
        }

        if (path == "/api/app/learning/test-app/client")
        {
            return Json(new Dictionary<string, string> { ["authority"] = Authority, ["clientId"] = ClientId, ["scope"] = Client.Scope });
        }

        if (path == "/connect/token")
        {
            var form = OidcClient.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (form["grant_type"] == "authorization_code")
            {
                if (form["code"] != IssuedCode) return Error("invalid_grant");
                LastCodeVerifier = form["code_verifier"];
                return Tokens(idToken: IdToken("user-1", "Ada"));
            }

            RefreshRequests++;
            if (HoldRefresh is { } hold) await hold.Task.WaitAsync(cancellationToken);
            return RefuseRefresh ? Error("invalid_grant") : Tokens(idToken: null);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Tokens(string? idToken)
    {
        var n = Interlocked.Increment(ref _tokens);
        var body = new Dictionary<string, object?>
        {
            ["access_token"] = $"access-{n}",
            ["refresh_token"] = $"refresh-{n}",
            ["expires_in"] = 3600,
            ["id_token"] = idToken,
        };
        return Json(body);
    }

    public static string IdToken(string sub, string name)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("{\"alg\":\"none\"}")}.{Part($"{{\"sub\":\"{sub}\",\"name\":\"{name}\"}}")}.sig";
    }

    private static HttpResponseMessage Error(string error) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent($"{{\"error\":\"{error}\"}}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

/// <summary>A browser that signs straight in: answers the authorize URL with a code and its state.</summary>
internal sealed class FakeFlow(FakeSignInService service, bool answerLater = false, string? wrongState = null) : ISignInFlow
{
    public Uri RedirectUri { get; } = new("http://127.0.0.1:47999/signin");

    public Uri? AuthorizeUrl { get; private set; }

    /// <summary>The answer the browser was given (for the web case, read again on the "next start").</summary>
    public string? Answer { get; private set; }

    public Task<string?> SignInAsync(Uri authorizeUrl, CancellationToken cancellationToken)
    {
        AuthorizeUrl = authorizeUrl;
        var state = wrongState ?? OidcClient.ParseQuery(authorizeUrl.Query)["state"];
        service.IssuedCode = "code-" + Guid.NewGuid().ToString("N");
        Answer = $"?code={service.IssuedCode}&state={Uri.EscapeDataString(state)}";
        return Task.FromResult(answerLater ? null : Answer);
    }
}
