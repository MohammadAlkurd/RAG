namespace RAG.Services.Ocr;

public class MinerUOptions
{
    public string Executable { get; set; } = "mineru-kit";
    public string Tier { get; set; } = "basic";
}