using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace RAG.Services.PdfTools;

public record PdfCheckResult(bool IsValid, int PageCount = 0, string? Error = null)
{
    public static PdfCheckResult Ok(int pages) => new(true, pages);
    public static PdfCheckResult Invalid(string error) => new(false, Error: error);
}

public static class PdfValidator
{
    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;

    public static bool HasPdfSignature(Stream stream)
    {
        Span<byte> header = stackalloc byte[PdfSignature.Length];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return read == header.Length && header.SequenceEqual(PdfSignature);
    }

    public static PdfCheckResult Check(Stream stream)
    {
        if (!HasPdfSignature(stream))
            return PdfCheckResult.Invalid("File is not a PDF.");

        try
        {
            using var document = PdfDocument.Open(stream);
            return PdfCheckResult.Ok(document.NumberOfPages);
        }
        catch (PdfDocumentEncryptedException)
        {
            return PdfCheckResult.Invalid("PDF is password-protected.");
        }
        catch (PdfDocumentFormatException)
        {
            return PdfCheckResult.Invalid("PDF is corrupt or unreadable.");
        }
        finally
        {
            stream.Position = 0;
        }
    }
}