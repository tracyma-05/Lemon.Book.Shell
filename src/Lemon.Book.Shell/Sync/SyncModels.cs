using System.Text.Json.Serialization;
using Lemon.Pet.Look;

namespace Lemon.Book.Shell.Sync;

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

/// <summary>
/// A child of the signed-in parent's account (the server's family list): whose progress this device syncs. Without
/// one the book syncs the account's own, as when the child signed in themselves.
/// </summary>
public sealed record SyncChild
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;
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

/// <summary>Source-generated JSON for sync (trimmed and AOT builds have no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, SyncJournalEntry>))]
[JsonSerializable(typeof(List<SyncEventRecord>))]
[JsonSerializable(typeof(SyncChild))]
[JsonSerializable(typeof(List<SyncChild>))]
[JsonSerializable(typeof(NewChildRequest))]
[JsonSerializable(typeof(PetInfo))]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncResponse))]
internal sealed partial class SyncJson : JsonSerializerContext;
