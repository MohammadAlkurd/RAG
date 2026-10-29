namespace RAG.Services.Ollama;

public class OllamaOptions
{
    public string Url { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "qwen3-embedding:0.6b";
}
