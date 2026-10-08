namespace RAG.Services.LocalPaths;

public static class DocumentPaths
{
    public static string UploadsDir => Path.Combine("data", "uploads");
    public static string Upload(Guid jobId) => Path.Combine(UploadsDir, $"{jobId}.pdf");

    public static string Dir(string hash) => Path.Combine("data", "markdown", hash);
    public static string Markdown(string hash) => Path.Combine(Dir(hash), $"{hash}.md"); 
}