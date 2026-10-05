namespace RAG.Services.Ingestion;

public record IngestionJob(Guid JobId, string OriginalFileName, string FilePath);