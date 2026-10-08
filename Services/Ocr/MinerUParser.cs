using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace RAG.Services.Ocr;

public class MinerUParser(ILogger<MinerUParser> logger,IOptions<MinerUOptions> options ) : IDocumentParser
{
    /// <summary>
    /// Runs MinerU on a PDF and writes Markdown with LaTeX formulas.
    /// </summary>
    /// <param name="pdfPath">Path to a validated PDF on disk.</param>
    /// <param name="outputMdPath">Where the final Markdown file should be written.</param>
    /// <param name="ct">Cancels the run and kills the MinerU process.</param>
    /// <returns>Full path of the generated <c>.md</c> file.</returns>
    /// <exception cref="InvalidOperationException">MinerU exited with a non-zero code or produced no Markdown.</exception>
    public async Task<string> ConvertToMarkdownAsync(string pdfPath, string outputMdPath, CancellationToken ct)
    {
        var outputDir = Path.GetDirectoryName(outputMdPath)!;
        Directory.CreateDirectory(outputDir);//Ensures the output directory exists

        var psi = new ProcessStartInfo(options.Value.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        
        psi.ArgumentList.Add("parse");
        psi.ArgumentList.Add(pdfPath);
        
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDir);
        
        psi.ArgumentList.Add("--tier");
        psi.ArgumentList.Add(options.Value.Tier);

        using var process = Process.Start(psi)!;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        logger.LogDebug("MinerU stdout: {Stdout}", stdout);

        if (process.ExitCode != 0)
        {
            var tail = stderr.Length > 2000 ? stderr[^2000..] : stderr;
            throw new InvalidOperationException($"MinerU exited with code {process.ExitCode}:\n{tail}");
        }
        var minerUFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(pdfPath) + ".md");
        if (!File.Exists(minerUFile))
            throw new InvalidOperationException($"MinerU produced no Markdown at {minerUFile}");
        File.Move(minerUFile, outputMdPath, overwrite: true);
        return outputMdPath;
    }
}