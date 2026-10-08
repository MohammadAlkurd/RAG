using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using RAG.Services.Ingestion;
using RAG.Services.LocalPaths;
using RAG.Services.PdfTools;

namespace RAG.Controllers;

[ApiController]
[Route("[controller]")]
public class DocumentController(IngestionStatus status , IngestionQueue queue) :ControllerBase
{

    [HttpPost]
    public async Task<ActionResult> AddDocument(IFormFile file)
    {
        if (status.IsBusy) return Conflict("Ingestion already running");
        
        if (file.Length == 0)
            return BadRequest("Empty file");
        
        using var stream = file.OpenReadStream();
        
        var check = PdfValidator.Check(stream);
        if (!check.IsValid)
            return BadRequest(check.Error);

        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        stream.Position = 0;
        
        var jobId = Guid.NewGuid();
        Directory.CreateDirectory(DocumentPaths.UploadsDir);
        var path = DocumentPaths.Upload(jobId);

        await using( var output = System.IO.File.Create(path)){
            await stream.CopyToAsync(output);
        }

        var res = queue.TryEnqueue( new IngestionJob(jobId,hash, file.FileName, path));
        if(res)
             return Accepted(new {JobId = jobId});
        System.IO.File.Delete(path);
        return Conflict("Queue is full");
    }
    
    [HttpGet("status")]
    public ActionResult<IngestionSnapshot> GetDocumentStatus()
    {
        return Ok(status.Current);
    }
}