using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lemon.Hub.Client;

/// <summary>How the app signs in, as the hub says: the sign-in service, the client and its scopes.</summary>
public sealed record SyncClientInfo(string Authority, string ClientId, string Scope);

/// <summary>The signed-in account on this device: its tokens and who it is.</summary>
public sealed record AccountSession
{
    public string AccessToken { get; init; } = string.Empty;

    public string? RefreshToken { get; init; }

    public DateTime ExpiresAt { get; init; }

    public string? UserName { get; init; }

    public string? UserId { get; init; }

    /// <summary>The sign-in service and client it came from, for refreshing.</summary>
    public string Authority { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;
}

/// <summary>A sign-in under way: what the answer must match, and how to finish it.</summary>
public sealed record PendingSignIn
{
    public string State { get; init; } = string.Empty;

    public string Verifier { get; init; } = string.Empty;

    public string RedirectUri { get; init; } = string.Empty;

    public string Authority { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string TokenEndpoint { get; init; } = string.Empty;

    public DateTime StartedAt { get; init; }
}

/// <summary>
/// The JSON a session and a pending sign-in are kept as (camelCase), for an <see cref="ISessionStore"/> of the app's
/// own, e.g. one that encrypts the text before it saves it. Unreadable text reads as null.
/// </summary>
public static class SessionJson
{
    public static string Serialize(AccountSession session) => JsonSerializer.Serialize(session, HubJson.Default.AccountSession);

    public static string Serialize(PendingSignIn pending) => JsonSerializer.Serialize(pending, HubJson.Default.PendingSignIn);

    public static AccountSession? ReadSession(string? json) => Read(json, HubJson.Default.AccountSession);

    public static PendingSignIn? ReadPending(string? json) => Read(json, HubJson.Default.PendingSignIn);

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
}

internal sealed record OidcDiscovery
{
    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; init; } = string.Empty;

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; init; } = string.Empty;
}

internal sealed record OidcTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed record IdTokenClaims
{
    [JsonPropertyName("sub")]
    public string? Sub { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("preferred_username")]
    public string? PreferredUserName { get; init; }
}

/// <summary>Source-generated JSON for sign-in (trimmed and AOT builds have no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncClientInfo))]
[JsonSerializable(typeof(AccountSession))]
[JsonSerializable(typeof(PendingSignIn))]
[JsonSerializable(typeof(OidcDiscovery))]
[JsonSerializable(typeof(OidcTokenResponse))]
[JsonSerializable(typeof(IdTokenClaims))]
internal sealed partial class HubJson : JsonSerializerContext;
