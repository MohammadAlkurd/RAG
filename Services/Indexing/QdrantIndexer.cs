using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RAG.Services.Chunking;
using static Qdrant.Client.Grpc.Conditions;

namespace RAG.Services.Indexing;

public class QdrantIndexer(IOptions<QdrantOptions> options)
{
    private readonly QdrantClient client = new(options.Value.Host, options.Value.Port);

    public async Task IndexAsync(string contentHash, IReadOnlyList<Chunk> chunks, IReadOnlyList<float[]> vectors, CancellationToken ct)
    {
        var collection = options.Value.Collection;
        if (!await client.CollectionExistsAsync(collection, ct))
            await client.CreateCollectionAsync(collection,
                new VectorParams { Size = (ulong)vectors[0].Length, Distance = Distance.Cosine }, cancellationToken: ct);

        await client.DeleteAsync(collection, MatchKeyword("ContentHash", contentHash), cancellationToken: ct);

        var points = chunks.Select((c, i) => new PointStruct
        {
            Id = Guid.NewGuid(),
            Vectors = vectors[i],
            Payload = { ["ContentHash"] = c.ContentHash, ["Index"] = c.Index, ["Heading"] = c.Heading, ["Text"] = c.Text },
        });
        foreach (var batch in points.Chunk(100))
            await client.UpsertAsync(collection, batch, cancellationToken: ct);
    }
}
