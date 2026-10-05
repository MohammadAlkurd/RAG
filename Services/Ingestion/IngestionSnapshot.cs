namespace RAG.Services.Ingestion;

public record IngestionSnapshot(
    IngestionState State,
    string? FileName = null,
    string? Progress = null,
    string? Error = null);