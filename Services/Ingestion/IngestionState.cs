namespace RAG.Services.Ingestion;

public enum IngestionState
{
    Idle,
    Queued,
    UnloadingLlm,
    Ocr,
    Chunking,
    Embedding,
    Indexing,
    Ready,
    Failed
}