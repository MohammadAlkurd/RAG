using System.Threading.Channels;

namespace RAG.Services.Ingestion;

public class IngestionQueue
{
   
    private readonly Channel<IngestionJob> _channel = Channel.CreateBounded<IngestionJob>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait});

    public bool TryEnqueue(IngestionJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<IngestionJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

}