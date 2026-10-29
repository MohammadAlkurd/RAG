using System.Text.Encodings.Web;
using System.Text.Json;
using RAG.Services.Chunking;
using RAG.Services.Indexing;
using RAG.Services.Ocr;
using RAG.Services.LocalPaths;
using RAG.Services.Ollama;
namespace RAG.Services.Ingestion;

public class IngestionWorker(IngestionQueue queue,IngestionStatus status ,
    ILogger<IngestionWorker> logger,
    IDocumentParser parser,
    FigureExtractor figures,
    MarkdownChunker chunker,
    OllamaClient ollama,
    QdrantIndexer index) :BackgroundService
{
    private const int EmbedBatchSize = 32;

    private static readonly JsonSerializerOptions ChunksJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                status.Start(job.OriginalFileName);
                logger.LogInformation("Processing job {JobId} {File}", job.JobId, job.OriginalFileName);
                
                status.Set(IngestionState.UnloadingLlm);
                await ollama.UnloadAllAsync(stoppingToken);
                status.Set(IngestionState.Ocr ,"Loading OCR model");
                var mdPath = DocumentPaths.Markdown(job.ContentHash);
                if (File.Exists(mdPath))
                    logger.LogInformation("OCR cached for {Hash}", job.ContentHash);
                else
                {
                    var progress = new Progress<string>(p => status.Set(IngestionState.Ocr, p));
                    await parser.ConvertToMarkdownAsync(job.FilePath, mdPath, progress, stoppingToken);
                }
                status.Set(IngestionState.Ocr, "Extracting figures");
                await figures.ExtractAsync(job.ContentHash, stoppingToken);
                status.Set(IngestionState.Chunking);
                var chunks = await chunker.ChunkFileAsync(mdPath, job.ContentHash, stoppingToken);
                await File.WriteAllTextAsync(DocumentPaths.Chunks(job.ContentHash),
                    JsonSerializer.Serialize(chunks, ChunksJson), stoppingToken);
                logger.LogInformation("Created {Count} chunks for {Hash}", chunks.Count, job.ContentHash);
                status.Set(IngestionState.Embedding, $"0/{chunks.Count} chunks");
                var vectors = new List<float[]>();
                foreach (var batch in chunks.Chunk(EmbedBatchSize))
                {
                    vectors.AddRange(await ollama.EmbedAsync(batch.Select(c => c.Text), stoppingToken));
                    status.Set(IngestionState.Embedding, $"{vectors.Count}/{chunks.Count} chunks");
                }
                status.Set(IngestionState.Indexing);
                await index.IndexAsync(job.ContentHash, chunks, vectors, stoppingToken);
                
                File.Move(job.FilePath, DocumentPaths.SourcePdf(job.ContentHash), overwrite: true);          
                status.Set(IngestionState.Ready);
                logger.LogInformation("Job {JobId} {File} completed", job.JobId, job.OriginalFileName);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                status.Fail(e.Message);
                logger.LogError(e, "Error processing job");
                
            }
        }
    }
}