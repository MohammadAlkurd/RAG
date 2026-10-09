using RAG.Services.Ocr;
using RAG.Services.LocalPaths;
namespace RAG.Services.Ingestion;

public class IngestionWorker(IngestionQueue queue,IngestionStatus status ,
    ILogger<IngestionWorker> logger,
    IDocumentParser parser) :BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                status.Start(job.OriginalFileName);
                logger.LogInformation("Processing job {JobId} {File}", job.JobId, job.OriginalFileName);
                
                status.Set(IngestionState.UnloadingLlm);
                await Task.Delay(1000, stoppingToken);
                status.Set(IngestionState.Ocr ,"Loading OCR model");
                var mdPath = DocumentPaths.Markdown(job.ContentHash);
                if (File.Exists(mdPath))
                    logger.LogInformation("OCR cached for {Hash}", job.ContentHash);
                else
                {
                    var progress = new Progress<string>(p => status.Set(IngestionState.Ocr, p));
                    await parser.ConvertToMarkdownAsync(job.FilePath, mdPath, progress, stoppingToken);
                }
                status.Set(IngestionState.Chunking);
                await Task.Delay(1000, stoppingToken);
                status.Set(IngestionState.Embedding);
                await Task.Delay(1000, stoppingToken);
                status.Set(IngestionState.Indexing);
                await Task.Delay(1000, stoppingToken);
                
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