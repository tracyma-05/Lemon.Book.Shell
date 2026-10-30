using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Lemon.Book.Shell.Explore.Narration;

/// <summary>
/// <c>narration.json</c>, written by the narration generator: per culture the voice settings, and per clip its hash,
/// length and sentence timings. The audio itself ships separately (release packs on the desktop, same-origin files on
/// the web), so the manifest says what exists and <see cref="INarrationAudio"/> says where it is.
/// </summary>
public sealed class NarrationManifest
{
    private readonly Dictionary<string, CultureEntry> _cultures;

    private NarrationManifest(Dictionary<string, CultureEntry> cultures)
    {
        _cultures = cultures;
    }

    public sealed record ClipEntry(string Hash, double Duration, IReadOnlyList<NarrationSentence> Sentences)
    {
        /// <summary>A story sentence's word timings; empty for other clips.</summary>
        public IReadOnlyList<NarrationSentence> Words { get; init; } = [];
    }

    public sealed record CultureEntry(string Voice, string Rate, string Pitch, IReadOnlyDictionary<string, ClipEntry> Clips);

    public static NarrationManifest Empty { get; } = new([]);

    private static readonly Lazy<NarrationManifest> s_bundled = new(LoadBundled);

    /// <summary>The manifest built into the app (read once).</summary>
    public static NarrationManifest Bundled => s_bundled.Value;

    /// <summary>
    /// Where a clip's audio is, relative to the narration folder: <c>&lt;culture&gt;/&lt;key&gt;.&lt;hash&gt;.mp3</c>. The hash
    /// changes with the text, so a file name never changes content.
    /// </summary>
    public static string ClipPath(string culture, string key, string hash) => $"{culture}/{key}.{hash}.mp3";

    /// <summary>Reads a manifest; broken JSON gives an empty one, so narration falls back to timed subtitles.</summary>
    public static NarrationManifest Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var cultures = new Dictionary<string, CultureEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var culture in document.RootElement.EnumerateObject())
            {
                var clips = new Dictionary<string, ClipEntry>(StringComparer.Ordinal);
                if (culture.Value.TryGetProperty("clips", out var clipsElement))
                {
                    foreach (var clip in clipsElement.EnumerateObject())
                    {
                        clips[clip.Name] = new ClipEntry(
                            clip.Value.GetProperty("hash").GetString() ?? string.Empty,
                            clip.Value.GetProperty("duration").GetDouble(),
                            Timings(clip.Value, "sentences"))
                        {
                            Words = Timings(clip.Value, "words"),
                        };
                    }
                }

                cultures[culture.Name] = new CultureEntry(
                    Text(culture.Value, "voice"), Text(culture.Value, "rate"), Text(culture.Value, "pitch"), clips);
            }

            return new NarrationManifest(cultures);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            Trace.TraceWarning($"The narration manifest could not be read; narration plays as subtitles only. {ex.Message}");
            return Empty;
        }
    }

    private static NarrationManifest LoadBundled()
    {
        // The book's own manifest (scripts/narration/generate-narration.py writes it; the book embeds it).
        var book = BookShell.AppAssembly;
        using var stream = book.GetManifestResourceStream($"{book.GetName().Name}.Narration.Manifest");
        if (stream is null)
        {
            return Empty;
        }

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public CultureEntry? Culture(string culture) => _cultures.GetValueOrDefault(culture);

    // [[start, end, text], ...] under name, or none.
    private static List<NarrationSentence> Timings(JsonElement clip, string name) =>
        clip.TryGetProperty(name, out var list)
            ? list.EnumerateArray()
                .Select(s => new NarrationSentence(s[0].GetDouble(), s[1].GetDouble(), s[2].GetString() ?? string.Empty))
                .ToList()
            : [];

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
}
