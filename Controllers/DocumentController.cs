using Microsoft.AspNetCore.Mvc;
using RAG.Services.Ingestion;
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

        var jobId = Guid.NewGuid();
        var dir = Path.Combine("data", "uploads");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{jobId}.pdf");

        await using( var output = System.IO.File.Create(path)){
            await stream.CopyToAsync(output);
        }

        var res = queue.TryEnqueue( new IngestionJob(jobId, file.FileName, path));
        if(res)
             return Accepted(new {JobId = jobId});
        System.IO.File.Delete(path);
        return Conflict("Queue is full");
    }
    
    [HttpGet("status")]
    public ActionResult GetDocumentStatus()
    {
        return Ok(status.Current);
    }
}