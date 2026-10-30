using System.Text.Json.Serialization;
using Lemon.Pet.Look;

namespace Lemon.Book.Shell.Sync;

/// <summary>How the book signs in, as the sync server says: the sign-in service, the client and its scopes.</summary>
public sealed record SyncClientInfo(string Authority, string ClientId, string Scope);

/// <summary>What a value's sync stands at on this device: the server version it was last in step with, and whether it changed here since.</summary>
public sealed record SyncJournalEntry
{
    public long Version { get; init; }

    public bool Dirty { get; init; }

    /// <summary>When it last changed here (UTC).</summary>
    public DateTime ChangedAt { get; init; }
}

/// <summary>Something the child did, waiting to be sent (see <see cref="SyncEvents"/>).</summary>
public sealed record SyncEventRecord
{
    public Guid Id { get; init; }

    public string Type { get; init; } = string.Empty;

    public string? Subject { get; init; }

    public string? Data { get; init; }

    public DateTime OccurredAt { get; init; }
}

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

/// <summary>
/// A child of the signed-in parent's account (the server's family list): whose progress this device syncs. Without
/// one the book syncs the account's own, as when the child signed in themselves.
/// </summary>
public sealed record SyncChild
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;
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

// The sync server's wire format (api/app/learning/{app}).

/// <summary>A child the parent adds from the book (<c>POST api/app/family/children</c>).</summary>
internal sealed record NewChildRequest(string Name);

internal sealed record SyncRequest
{
    public string DeviceId { get; init; } = string.Empty;

    public string? DeviceName { get; init; }

    public string? Platform { get; init; }

    public string? AppVersion { get; init; }

    public long Since { get; init; }

    public List<SyncChange> Changes { get; init; } = [];

    public List<SyncEventRecord> Events { get; init; } = [];
}

internal sealed record SyncChange
{
    public string Key { get; init; } = string.Empty;

    public string? Value { get; init; }

    public long BaseVersion { get; init; }

    public DateTime ChangedAt { get; init; }
}

internal sealed record SyncRecord
{
    public string Key { get; init; } = string.Empty;

    public string? Value { get; init; }

    public bool Deleted { get; init; }

    public long Version { get; init; }

    public DateTime ChangedAt { get; init; }

    public string? DeviceId { get; init; }
}

internal sealed record SyncAccepted
{
    public string Key { get; init; } = string.Empty;

    public long Version { get; init; }
}

internal sealed record SyncResponse
{
    public long Cursor { get; init; }

    public List<SyncAccepted> Accepted { get; init; } = [];

    public List<SyncRecord> Conflicts { get; init; } = [];

    public List<SyncRecord> Changes { get; init; } = [];
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

/// <summary>Source-generated JSON for sync and sign-in (trimmed and AOT builds have no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncClientInfo))]
[JsonSerializable(typeof(Dictionary<string, SyncJournalEntry>))]
[JsonSerializable(typeof(List<SyncEventRecord>))]
[JsonSerializable(typeof(AccountSession))]
[JsonSerializable(typeof(PendingSignIn))]
[JsonSerializable(typeof(SyncChild))]
[JsonSerializable(typeof(List<SyncChild>))]
[JsonSerializable(typeof(NewChildRequest))]
[JsonSerializable(typeof(PetInfo))]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(OidcDiscovery))]
[JsonSerializable(typeof(OidcTokenResponse))]
[JsonSerializable(typeof(IdTokenClaims))]
internal sealed partial class SyncJson : JsonSerializerContext;
