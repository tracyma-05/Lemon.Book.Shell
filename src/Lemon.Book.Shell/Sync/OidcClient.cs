using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lemon.Book.Shell.Sync;

/// <summary>Sign-in failed or was refused; <see cref="Exception.Message"/> is for the log, not the reader.</summary>
public sealed class SignInException(string message, bool needsSignIn = false) : Exception(message)
{
    /// <summary>The account's sign-in is no longer good (refused refresh): the child has to sign in again.</summary>
    public bool NeedsSignIn { get; } = needsSignIn;
}

/// <summary>
/// The OpenID Connect pieces the book needs, as a public client: the authorization code flow with PKCE, and refreshing.
/// No client secret (an app cannot keep one); the sign-in page is the sign-in service's own.
/// </summary>
public sealed class OidcClient(HttpClient http)
{
    public async Task<(PendingSignIn Pending, Uri AuthorizeUrl)> BeginAsync(
        SyncClientInfo client, Uri redirectUri, DateTime now, CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(client.Authority, cancellationToken);
        var verifier = RandomText(32);
        var state = RandomText(16);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var query = string.Join('&', new[]
        {
            ("client_id", client.ClientId),
            ("response_type", "code"),
            ("scope", client.Scope),
            ("redirect_uri", redirectUri.ToString()),
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
        }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        var separator = discovery.AuthorizationEndpoint.Contains('?') ? '&' : '?';

        var pending = new PendingSignIn
        {
            State = state,
            Verifier = verifier,
            RedirectUri = redirectUri.ToString(),
            Authority = client.Authority,
            ClientId = client.ClientId,
            TokenEndpoint = discovery.TokenEndpoint,
            StartedAt = now,
        };
        return (pending, new Uri(discovery.AuthorizationEndpoint + separator + query));
    }

    /// <summary>Reads the answer (<c>?code=…&amp;state=…</c>) and trades the code for tokens.</summary>
    public async Task<AccountSession> CompleteAsync(PendingSignIn pending, string callbackQuery, DateTime now, CancellationToken cancellationToken)
    {
        var answer = ParseQuery(callbackQuery);
        if (answer.TryGetValue("error", out var error))
        {
            throw new SignInException($"Sign-in refused: {error}");
        }

        if (!answer.TryGetValue("state", out var state) || state != pending.State)
        {
            throw new SignInException("Sign-in answer does not match the request (state).");
        }

        if (!answer.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw new SignInException("Sign-in answer has no code.");
        }

        var tokens = await TokenAsync(pending.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = pending.ClientId,
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["code_verifier"] = pending.Verifier,
        }, cancellationToken);

        var claims = ReadIdToken(tokens.IdToken);
        return new AccountSession
        {
            AccessToken = tokens.AccessToken!,
            RefreshToken = tokens.RefreshToken,
            ExpiresAt = now.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600),
            UserName = claims?.Name ?? claims?.PreferredUserName,
            UserId = claims?.Sub,
            Authority = pending.Authority,
            ClientId = pending.ClientId,
        };
    }

    /// <summary>A new access token from the refresh token; the service may hand out a new refresh token too.</summary>
    public async Task<AccountSession> RefreshAsync(AccountSession session, DateTime now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(session.RefreshToken))
        {
            throw new SignInException("No refresh token.", needsSignIn: true);
        }

        var discovery = await DiscoverAsync(session.Authority, cancellationToken);
        var tokens = await TokenAsync(discovery.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = session.ClientId,
            ["refresh_token"] = session.RefreshToken,
        }, cancellationToken);

        return session with
        {
            AccessToken = tokens.AccessToken!,
            RefreshToken = tokens.RefreshToken ?? session.RefreshToken,
            ExpiresAt = now.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600),
        };
    }

    private async Task<OidcDiscovery> DiscoverAsync(string authority, CancellationToken cancellationToken)
    {
        var address = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        await using var stream = await http.GetStreamAsync(address, cancellationToken);
        var discovery = await JsonSerializer.DeserializeAsync(stream, SyncJson.Default.OidcDiscovery, cancellationToken);
        return discovery is { AuthorizationEndpoint.Length: > 0, TokenEndpoint.Length: > 0 }
            ? discovery
            : throw new SignInException("The sign-in service's discovery document has no endpoints.");
    }

    private async Task<OidcTokenResponse> TokenAsync(string endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        OidcTokenResponse? tokens = null;
        try
        {
            tokens = JsonSerializer.Deserialize(body, SyncJson.Default.OidcTokenResponse);
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's error page): reported below by status.
        }

        if (response.IsSuccessStatusCode && tokens?.AccessToken is { Length: > 0 })
        {
            return tokens;
        }

        // invalid_grant: the refresh token expired or was revoked, or the code was used already.
        var refused = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                      && tokens?.Error is "invalid_grant" or "invalid_client" or "unauthorized_client";
        throw new SignInException($"Token request failed: {(int)response.StatusCode} {tokens?.Error}", needsSignIn: refused);
    }

    /// <summary>The id token's claims (who signed in). Not verified: it came straight from the token endpoint over TLS.</summary>
    internal static IdTokenClaims? ReadIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize(Convert.FromBase64String(payload), SyncJson.Default.IdTokenClaims);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            var name = Uri.UnescapeDataString((at < 0 ? pair : pair[..at]).Replace('+', ' '));
            var value = at < 0 ? string.Empty : Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
            values.TryAdd(name, value);
        }

        return values;
    }

    private static string RandomText(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
