namespace RAG.Services.Ocr;

public interface IDocumentParser
{
    Task<string> ConvertToMarkdownAsync(string pdfPath, string outputDir,IProgress<string>? progress, CancellationToken ct);
}