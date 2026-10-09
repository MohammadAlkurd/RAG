using System.Text.RegularExpressions;
using RAG.Services.LocalPaths;

namespace RAG.Services.Ocr;

/// <summary>
/// Moves base64 images embedded by MinerU out of the Markdown into <c>images/fig-NNNN.ext</c>
/// and replaces them with relative links. Safe to run repeatedly: a cleaned file is left untouched.
/// </summary>
public partial class FigureExtractor(ILogger<FigureExtractor> logger)
{
    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\(data:image/(?<ext>[a-z]+);base64,(?<data>[^)]+)\)")]
    private static partial Regex EmbeddedImageRegex();

    /// <returns>Number of images extracted.</returns>
    public async Task<int> ExtractAsync(string contentHash, CancellationToken ct)
    {
        var mdPath = DocumentPaths.Markdown(contentHash);
        var imagesDir = DocumentPaths.ImagesDir(contentHash);
        var tmpPath = mdPath + ".tmp";
        var count = 0;

        await using (var writer = new StreamWriter(tmpPath))
        {
            await foreach (var line in File.ReadLinesAsync(mdPath, ct))
            {
                if (!line.Contains("data:image", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(line);
                    continue;
                }

                Directory.CreateDirectory(imagesDir);
                var replaced = EmbeddedImageRegex().Replace(line, m =>
                {
                    var ext = m.Groups["ext"].Value == "jpeg" ? "jpg" : m.Groups["ext"].Value;
                    var name = $"fig-{++count:D4}.{ext}";
                    File.WriteAllBytes(Path.Combine(imagesDir, name), Convert.FromBase64String(m.Groups["data"].Value));
                    return $"![{m.Groups["alt"].Value}](images/{name})";
                });
                await writer.WriteLineAsync(replaced);
            }
        }

        if (count == 0)
        {
            File.Delete(tmpPath);
            return 0;
        }

        File.Move(tmpPath, mdPath, overwrite: true);
        logger.LogInformation("Extracted {Count} figures for {Hash}", count, contentHash);
        return count;
    }
}
