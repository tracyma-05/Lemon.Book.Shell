using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

using Lemon.Book.Shell.Infrastructures.Localization;

namespace Lemon.Book.Shell.Updates;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>Outcome of one update check.</summary>
/// <param name="Latest">The published release; null when the check failed.</param>
/// <param name="Error">Why the check failed, in words a user can act on; null otherwise.</param>
/// <param name="DownloadUrl">
/// What the download button opens: the package for this platform, or the release page when the release has
/// none for it. Set whenever <see cref="Status"/> is <see cref="UpdateCheckStatus.UpdateAvailable"/>.
/// </param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    UpdateManifest? Latest,
    string? Error,
    Uri? DownloadUrl = null)
{
    public static UpdateCheckResult Failure(Version current, string error) =>
        new(UpdateCheckStatus.Failed, current, null, error);
}

/// <summary>
/// Reads the latest release (a manifest, or a GitHub Release, see <see cref="UpdateFeed"/>), compares it with this
/// version and picks the package for this computer. Network and format problems do not throw: they come back as
/// <see cref="UpdateCheckStatus.Failed"/> with a sentence the reader can act on.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // One for the whole process: a new HttpClient per check would run out of sockets.
    private static readonly Lazy<HttpClient> SharedClient = new(() => CreateClient(RequestTimeout, acceptJson: true));

    private readonly HttpClient _httpClient;
    private readonly string _platform;

    public UpdateService()
        : this(SharedClient.Value, ResolveCurrentVersion())
    {
    }

    /// <summary>For tests: a fake transport, a fixed installed version and optionally a platform.</summary>
    internal UpdateService(HttpClient httpClient, Version currentVersion, string? platform = null)
    {
        _httpClient = httpClient;
        CurrentVersion = currentVersion;
        _platform = platform ?? UpdatePlatform.Current;
    }

    public Version CurrentVersion { get; }

    public async Task<UpdateCheckResult> CheckAsync(UpdateSource? source, CancellationToken cancellationToken = default)
    {
        var localization = LocalizationService.Instance;
        if (source is null)
        {
            return UpdateCheckResult.Failure(CurrentVersion, localization.GetString("Update_Error_NoFeed"));
        }

        var manifestUri = source.Address;
        var isGitHub = source.IsGitHub;
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);

            // Manifests often carry a short max-age; a caching proxy would hide a release just published.
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (isGitHub)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // GitHub answers 404 before the first release, or for a private repository: nothing to download.
                return UpdateCheckResult.Failure(
                    CurrentVersion,
                    isGitHub && response.StatusCode == HttpStatusCode.NotFound
                        ? localization.GetString("Update_Error_NoRelease")
                        : localization.Format("Update_Error_Http", (int)response.StatusCode));
            }

            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A TaskCanceledException without a cancellation is HttpClient's own timeout.
            return UpdateCheckResult.Failure(CurrentVersion, localization.GetString("Update_Error_Offline"));
        }

        var manifest = isGitHub ? ParseGitHubRelease(json) : ParseManifest(json, manifestUri);
        if (manifest is null)
        {
            return UpdateCheckResult.Failure(CurrentVersion, localization.GetString("Update_Error_Unreadable"));
        }

        if (Normalize(manifest.Version) <= Normalize(CurrentVersion))
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, CurrentVersion, manifest, null);
        }

        var downloadUrl = SelectDownload(manifest, UpdatePlatform.Candidates(_platform, manifest.Downloads.Keys));
        if (downloadUrl is null)
        {
            return UpdateCheckResult.Failure(
                CurrentVersion, localization.Format("Update_Error_NoPackage", manifest.Version.ToString(3), _platform));
        }

        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, CurrentVersion, manifest, null, downloadUrl);
    }

    /// <summary>
    /// The package for the first matching platform candidate when the release lists packages per platform;
    /// the single <c>downloadUrl</c> when it does not; the release page when nothing else fits.
    /// </summary>
    internal static Uri? SelectDownload(UpdateManifest manifest, IReadOnlyList<string> candidates)
    {
        if (manifest.Downloads.Count > 0)
        {
            return UpdatePlatform.Select(manifest.Downloads, candidates) ?? manifest.PageUrl;
        }

        return manifest.DownloadUrl ?? manifest.PageUrl;
    }

    /// <summary>
    /// Parses the manifest JSON (see <see cref="UpdateManifest"/>); null when it is malformed or lacks a
    /// usable version or any link.
    /// </summary>
    internal static UpdateManifest? ParseManifest(string json, Uri manifestUri)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            var version = TryParseVersion(GetString(root, "version"));
            var downloadUrl = ResolveLink(GetString(root, "downloadUrl"), manifestUri);
            var pageUrl = ResolveLink(GetString(root, "pageUrl"), manifestUri);

            var downloads = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
            var checksums = new Dictionary<Uri, string>();
            if (TryGetProperty(root, "downloads", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in map.EnumerateObject())
                {
                    // A plain link, or { "url": "…", "sha256": "…" }.
                    var (link, sha256) = entry.Value.ValueKind switch
                    {
                        JsonValueKind.String => (ResolveLink(entry.Value.GetString(), manifestUri), null),
                        JsonValueKind.Object => (ResolveLink(GetString(entry.Value, "url"), manifestUri),
                            NormalizeSha256(GetString(entry.Value, "sha256"))),
                        _ => (null, null),
                    };
                    if (link is not null && !string.IsNullOrWhiteSpace(entry.Name))
                    {
                        downloads[entry.Name.Trim().ToLowerInvariant()] = link;
                        if (sha256 is not null)
                        {
                            checksums[link] = sha256;
                        }
                    }
                }
            }

            // Some servers list each package's checksum by platform next to "downloads".
            if (TryGetProperty(root, "checksums", out var sums) && sums.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in sums.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String &&
                        downloads.TryGetValue(entry.Name.Trim(), out var link) &&
                        NormalizeSha256(entry.Value.GetString()) is { } sha256)
                    {
                        checksums.TryAdd(link, sha256);
                    }
                }
            }

            // The top-level checksum describes the release's single file: downloadUrl, and the one package
            // when only one is listed. With several packages it cannot be told which one it belongs to.
            if (NormalizeSha256(GetString(root, "sha256")) is { } releaseSha256)
            {
                if (downloadUrl is not null)
                {
                    checksums.TryAdd(downloadUrl, releaseSha256);
                }

                if (downloads.Count == 1)
                {
                    checksums.TryAdd(downloads.Values.First(), releaseSha256);
                }
            }

            if (version is null || (downloadUrl is null && pageUrl is null && downloads.Count == 0))
            {
                return null;
            }

            return new UpdateManifest(version, downloadUrl, GetString(root, "releaseNotes"), ParseDate(GetString(root, "publishedAt")))
            {
                Downloads = downloads,
                PageUrl = pageUrl,
                Checksums = checksums,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a GitHub "latest release" response: the tag is the version (<c>v1.2.0</c>), the body the release
    /// notes, and each package asset is filed under the platform its name mentions
    /// (<c>MyApp-1.2.0-osx-arm64.zip</c>, see <see cref="UpdatePlatform.FromFileName"/>).
    /// </summary>
    /// <returns>Null when the response is malformed or the tag is not a version.</returns>
    internal static UpdateManifest? ParseGitHubRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var version = TryParseVersion(GetString(root, "tag_name")) ?? TryParseVersion(GetString(root, "name"));
            if (version is null)
            {
                return null;
            }

            var packages = new List<(string? Platform, int Rank, Uri Link)>();
            var checksums = new Dictionary<Uri, string>();
            if (TryGetProperty(root, "assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = GetString(asset, "name");
                    var link = ResolveLink(GetString(asset, "browser_download_url"), null);
                    if (name is null || link is null || !UpdatePlatform.IsPackage(name))
                    {
                        continue;
                    }

                    packages.Add((UpdatePlatform.FromFileName(name), UpdatePlatform.PackageRank(name), link));

                    // GitHub computes "digest": "sha256:…" for every asset uploaded since mid-2025.
                    var digest = GetString(asset, "digest");
                    if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                        NormalizeSha256(digest["sha256:".Length..]) is { } sha256)
                    {
                        checksums[link] = sha256;
                    }
                }
            }

            var downloads = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in packages.Where(x => x.Platform is not null).GroupBy(x => x.Platform!))
            {
                downloads[group.Key] = group.OrderBy(x => x.Rank).First().Link;
            }

            // A release with a single package that names no platform: offer it to everyone.
            if (downloads.Count == 0 && packages.Count == 1)
            {
                downloads[UpdatePlatform.Any] = packages[0].Link;
            }

            return new UpdateManifest(version, null, GetString(root, "body"), ParseDate(GetString(root, "published_at")))
            {
                Downloads = downloads,
                PageUrl = ResolveLink(GetString(root, "html_url"), null),
                Checksums = checksums,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Accepts <c>1.2</c>, <c>1.2.3</c>, <c>1.2.3.4</c>, an optional leading <c>v</c>, and ignores
    /// SemVer pre-release / build suffixes (<c>1.2.3-beta+abc</c> reads as 1.2.3).
    /// </summary>
    internal static Version? TryParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            value = value[..suffix];
        }

        return Version.TryParse(value, out var version) ? Normalize(version) : null;
    }

    /// <summary>
    /// <see cref="Version"/> treats missing components as -1, so 1.2 would sort below 1.2.0.
    /// Filling them with zero makes the comparison mean what people expect.
    /// </summary>
    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>An http(s) link, resolved against <paramref name="baseUri"/> when relative and a base is given.</summary>
    private static Uri? ResolveLink(string? value, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Uri? uri;
        var parsed = baseUri is null
            ? Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri)
            : Uri.TryCreate(baseUri, value.Trim(), out uri);
        if (!parsed || uri is null)
        {
            return null;
        }

        // The link ends up in the default browser; anything but http(s) could launch arbitrary handlers.
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    /// <summary>64 hex digits, lower-cased; null for anything else.</summary>
    internal static string? NormalizeSha256(string? value)
    {
        var text = value?.Trim();
        return text is { Length: 64 } && text.All(Uri.IsHexDigit) ? text.ToLowerInvariant() : null;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Version ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
        return Normalize(assembly.GetName().Version ?? new Version(1, 0, 0));
    }

    /// <summary>A client that names the app in its User-Agent; also used by <see cref="UpdateInstaller"/> for downloads.</summary>
    internal static HttpClient CreateClient(TimeSpan timeout, bool acceptJson)
    {
        // GitHub's API refuses requests without a User-Agent, so every request names the app.
        var client = new HttpClient { Timeout = timeout };

        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
        var name = assembly.GetName();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            new ProductHeaderValue(name.Name ?? "App", name.Version?.ToString(3) ?? "1.0.0")));
        if (acceptJson)
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        return client;
    }
}
