using System.Globalization;

namespace Lemon.Book.Shell.Explore.Narration;

/// <summary>One sentence of a clip and when it is spoken, in seconds from the clip's start.</summary>
public sealed record NarrationSentence(double Start, double End, string Text);

/// <summary>
/// A narration clip in one language: its length and sentence timings (for the subtitles), and whether there is audio.
/// A clip without audio (not rendered yet, or voice turned off) still has timings, estimated from the text. A story
/// sentence's clip has word timings too (<see cref="Words"/>, without the punctuation), to light up what is being said.
/// </summary>
public sealed record NarrationClip(
    string Key, double Duration, IReadOnlyList<NarrationSentence> Sentences, bool HasAudio, IReadOnlyList<NarrationSentence>? Words = null)
{
    /// <summary>The sentence being spoken at <paramref name="seconds"/>, or the last one once it is over.</summary>
    public string SentenceAt(double seconds)
    {
        if (Sentences.Count == 0) return string.Empty;
        foreach (var sentence in Sentences)
        {
            if (seconds < sentence.End) return sentence.Text;
        }

        return Sentences[^1].Text;
    }

    /// <summary>Timings from the text alone: a reading pace per language, split at sentence ends.</summary>
    public static NarrationClip Estimate(string key, string text, CultureInfo culture)
    {
        // About 4.5 Chinese characters or 14 Latin characters a second at the voices' -10 % rate.
        var perSecond = culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 4.5 : 14;
        var sentences = new List<NarrationSentence>();
        var start = 0.3;
        foreach (var part in SplitSentences(text))
        {
            var length = (part.Length / perSecond) + 0.25;
            sentences.Add(new NarrationSentence(start, start + length, part));
            start += length;
        }

        return new NarrationClip(key, start + 0.4, sentences, false);
    }

    internal static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var end = c is '。' or '！' or '？' or '!' or '?'
                      || (c == '.' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])));
            if (!end) continue;

            // Keep a closing quote or bracket with its sentence.
            while (i + 1 < text.Length && text[i + 1] is '”' or '"' or '’' or '）' or ')') i++;
            var part = text[start..(i + 1)].Trim();
            if (part.Length > 0) yield return part;
            start = i + 1;
        }

        var rest = text[start..].Trim();
        if (rest.Length > 0) yield return rest;
    }
}
