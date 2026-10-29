using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace RAG.Services.Ollama;

public class OllamaClient(HttpClient http, IOptions<OllamaOptions> options)
{
    public async Task<float[][]> EmbedAsync(IEnumerable<string> texts, CancellationToken ct)
    {
        var response = await PostAsync("api/embed", new { model = options.Value.EmbeddingModel, input = texts }, ct);
        return (await response.Content.ReadFromJsonAsync<EmbedResponse>(ct))!.Embeddings;
    }

    public async Task UnloadAllAsync(CancellationToken ct)
    {
        var loaded = await http.GetFromJsonAsync<LoadedModels>($"{options.Value.Url}/api/ps", ct);
        foreach (var model in loaded!.Models)
            await PostAsync("api/generate", new { model = model.Name, keep_alive = 0 }, ct);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync($"{options.Value.Url}/{path}", body, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama /{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return response;
    }

    private record EmbedResponse(float[][] Embeddings);
    private record LoadedModels(List<LoadedModel> Models);
    private record LoadedModel(string Name);
}
