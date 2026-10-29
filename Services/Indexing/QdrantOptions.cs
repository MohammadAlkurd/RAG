namespace RAG.Services.Indexing;

public class QdrantOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6334;
    public string Collection { get; set; } = "chunks";
}
