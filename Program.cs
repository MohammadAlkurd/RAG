using System.Text.Json.Serialization;
using RAG.Services.Ingestion;
using RAG.Services.Ocr;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(); // Native .NET spec generator
builder.Services.AddSingleton<IngestionStatus>();
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.Configure<MinerUOptions>(builder.Configuration.GetSection("MinerU"));
builder.Services.AddSingleton<IDocumentParser, MinerUParser>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(); // Accessible via /scalar by default
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        foreach (var url in app.Urls)
            app.Logger.LogInformation("Scalar API reference: {Url}/scalar", url);
    });
}
app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();