using System.Text.Json.Serialization;
using Lemon.Book.Shell.Contents;
using Lemon.Book.Shell.Explore.Narration;

namespace Lemon.Book.Shell.Infrastructures.Settings;

/// <summary>
/// Source-generated JSON for everything the shell saves (a book keeps its own context for its own records), so saving works without reflection (trimmed and
/// native AOT builds turn reflection-based serialization off).
/// </summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(NarrationSettings))]
[JsonSerializable(typeof(List<BookmarkRecord>))]
[JsonSerializable(typeof(List<NoteRecord>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(Dictionary<string, ChapterProgressRecord>))]
[JsonSerializable(typeof(Dictionary<string, List<int>>))]
internal sealed partial class BookJson : JsonSerializerContext;
