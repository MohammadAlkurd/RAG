using System.Text.RegularExpressions;

namespace RAG.Services.Chunking;

public record Chunk(string ContentHash, int Index, string Heading, string Text);

public partial class MarkdownChunker
{
    private const int MinWords = 30;
    private const int MaxWords = 800;

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreakRegex();

    [GeneratedRegex(@"^#{1,6}\s+(?<title>.+)")]
    private static partial Regex HeadingRegex();

    private sealed record Section(string Heading, List<string> Paragraphs)
    {
        public int Words => Paragraphs.Sum(CountWords);
    }

    /// <summary>
    /// Chunks a md file into smaller chunks
    /// </summary>
    /// <param name="filePath">the path of the md file</param>
    /// <param name="contentHash">the hash of the .md file</param>
    /// <param name="ct">Cancels reading the file.</param>
    /// <returns>a list of chunks to later send to the vector db</returns>
    public async Task<List<Chunk>> ChunkFileAsync(string filePath, string contentHash, CancellationToken ct)
    {
        var text = (await File.ReadAllTextAsync(filePath, ct)).Replace("\r\n", "\n");
        var paragraphs = ParagraphBreakRegex().Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0);

        var chunks = new List<Chunk>();
        foreach (var section in MergeSmall(SplitIntoSections(paragraphs)))
        foreach (var body in SplitLarge(section.Paragraphs))
        {
            var chunkText = section.Heading.Length > 0 ? $"{section.Heading}\n\n{body}" : body;
            chunks.Add(new Chunk(contentHash, chunks.Count, section.Heading, chunkText));
        }
        return chunks;
    }

    private static IEnumerable<Section> SplitIntoSections(IEnumerable<string> paragraphs)
    {
        var current = new Section("", []);
        foreach (var p in paragraphs)
        {
            if (HeadingRegex().Match(p) is { Success: true } h)
            {
                if (current.Paragraphs.Count > 0 || current.Heading.Length > 0) yield return current;
                current = new Section(h.Groups["title"].Value.Trim(), []);
            }
            else
            {
                current.Paragraphs.Add(p);
            }
        }
        if (current.Paragraphs.Count > 0 || current.Heading.Length > 0) yield return current;
    }

    /// <summary>Joins sections under <see cref="MinWords"/> (e.g. chapter titles) onto the start of the next one.</summary>
    private static IEnumerable<Section> MergeSmall(IEnumerable<Section> sections)
    {
        Section? carry = null;
        foreach (var section in sections)
        {
            var merged = carry is null
                ? section
                : new Section(JoinHeadings(carry.Heading, section.Heading), [.. carry.Paragraphs, .. section.Paragraphs]);

            if (merged.Words < MinWords)
            {
                carry = merged;
                continue;
            }
            carry = null;
            yield return merged;
        }
        if (carry is not null) yield return carry;
    }

    /// <summary>Cuts sections over <see cref="MaxWords"/> at paragraph boundaries. A single oversized paragraph stays whole.</summary>
    private static IEnumerable<string> SplitLarge(List<string> paragraphs)
    {
        var buffer = new List<string>();
        var words = 0;
        foreach (var p in paragraphs)
        {
            var w = CountWords(p);
            if (buffer.Count > 0 && words + w > MaxWords)
            {
                yield return string.Join("\n\n", buffer);
                buffer.Clear();
                words = 0;
            }
            buffer.Add(p);
            words += w;
        }
        if (buffer.Count > 0) yield return string.Join("\n\n", buffer);
    }

    private static string JoinHeadings(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : $"{a} > {b}";

    private static int CountWords(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
