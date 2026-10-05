using RAG.Services.Ingestion;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddOpenApi(); // Native .NET spec generator
builder.Services.AddSingleton<IngestionStatus>();
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddHostedService<IngestionWorker>();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(); // Accessible via /scalar by default
}
app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();