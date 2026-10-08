using System.Security.Cryptography;

namespace RAG.Services.Ingestion;

public record IngestionJob(
    Guid JobId, 
    string ContentHash,
    string OriginalFileName,
    string FilePath);