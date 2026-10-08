# RAG Service for STEM Textbooks

A self-hosted retrieval-augmented generation (RAG) backend for STEM textbook PDFs, written in C# on ASP.NET Core (.NET 10).

> **Status: early development.** Upload, validation, queueing, job-status reporting and **OCR to Markdown + LaTeX (via MinerU)** work today. The chunking, embedding and indexing stages are still **placeholders** that only move the job through its states. Retrieval and question answering haven't been started.

## What works today

- **PDF upload API.** `POST /Document` accepts a multipart file upload.
- **Upload validation** (`Services/PdfTools/PdfValidator.cs`):
  - checks the `%PDF-` magic-byte signature without trusting the file extension or content type
  - opens the document with [PdfPig](https://github.com/UglyToad/PdfPig) to reject password-protected and corrupt files
  - rewinds the stream so the caller can still persist the file afterwards
- **Background ingestion.** Valid files are saved to `data/uploads/{jobId}.pdf` and handed to a `BackgroundService` worker through a bounded `System.Threading.Channels` queue.
- **OCR with [MinerU](https://github.com/opendatalab/MinerU).** The worker runs `mineru-kit` as a child process and converts the PDF to Markdown, with formulas preserved as LaTeX.
- **Content-hash caching.** Every upload is hashed (SHA-256). The Markdown is stored at `data/markdown/<hash>/<hash>.md`. If that file already exists, the OCR stage is skipped, so re-uploading a book only re-runs the later stages.
- **Job status.** `GET /Document/status` returns the current state of the ingestion job, with enum values serialized as strings.
- **OpenAPI + Scalar** API reference UI in the Development environment. The link is printed in the log at startup.

## Architecture

```
            POST /Document
                  │
                  ▼
        ┌───────────────────┐   invalid → 400
        │ DocumentController│── busy    → 409
        └─────────┬─────────┘
                  │ PdfValidator.Check
                  │ SHA-256 content hash
                  │ save to data/uploads/
                  ▼
        ┌───────────────────┐
        │  IngestionQueue   │  bounded Channel<IngestionJob>, capacity 1
        └─────────┬─────────┘
                  ▼
        ┌────────────────────┐        ┌─────────────────────┐
        │  IngestionWorker   │──────▶ │   IngestionStatus   │ ◀── GET /Document/status
        │ (BackgroundService)│ writes │ (immutable snapshot)│
        └─────────┬──────────┘        └─────────────────────┘
                  │ Ocr stage (skipped if data/markdown/<hash>/<hash>.md exists)
                  ▼
        ┌────────────────────┐
        │  IDocumentParser   │  MinerUParser → runs mineru-kit
        └────────────────────┘
```

### Ingestion state machine

`IngestionState` defines how a job moves through the pipeline:

```
Idle → Queued → UnloadingLlm → Ocr → Chunking → Embedding → Indexing → Ready
                                     (any stage) ───────────────────▶ Failed
```

Every job goes through all stages. A stage is skipped when its output already exists (currently only `Ocr`, through the Markdown cache).

`IngestionStatus` stores the current state as an immutable `IngestionSnapshot` record behind a `volatile` field. The worker swaps in a new snapshot on each transition, so readers never see a half-updated status. The service handles one document at a time. While a job is running, new uploads get `409 Conflict`.

## Project layout

```
Controllers/
  DocumentController.cs      upload + status endpoints
Services/
  PdfTools/
    PdfValidator.cs          signature, encryption and corruption checks
  Ingestion/
    IngestionJob.cs          job record (id, content hash, original name, stored path)
    IngestionQueue.cs        bounded channel wrapper
    IngestionWorker.cs       BackgroundService that runs the pipeline
    IngestionStatus.cs       thread-safe current-status holder
    IngestionSnapshot.cs     immutable status record
    IngestionState.cs        pipeline states
  Ocr/
    IDocumentParser.cs       PDF → Markdown abstraction
    MinerUParser.cs          runs the MinerU CLI as a child process
    MinerUOptions.cs         "MinerU" configuration section
  LocalPaths/
    DocumentPaths.cs         single place for all data/ paths
Program.cs                   DI registration and middleware
```

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Python 3.12 and MinerU (see below)
- An NVIDIA GPU is strongly recommended for OCR

### MinerU setup

MinerU runs as an external process, so it has to be installed separately.

1. Create a Python 3.12 virtual environment and install MinerU:

   ```bash
   python3.12 -m venv ~/mineru-env
   ~/mineru-env/bin/pip install mineru
   ```

   On Windows the executables are in `mineru-env\Scripts\` instead of `mineru-env/bin/`.

2. Download the models for the tier you'll use, then verify them:

   ```bash
   ~/mineru-env/bin/mineru-models-download --tier basic
   ~/mineru-env/bin/mineru-kit models verify
   ```

3. Point the app at the executable. This path is machine-specific, so keep it in user secrets rather than in `appsettings.json`:

   ```bash
   dotnet user-secrets set "MinerU:Executable" "$HOME/mineru-env/bin/mineru-kit"
   # Windows: dotnet user-secrets set "MinerU:Executable" "C:\path\to\mineru-env\Scripts\mineru-kit.exe"
   ```

   If `mineru-kit` is already on your `PATH`, you can skip this step. The default value is `mineru-kit`.

#### Configuration

| Key                 | Default      | Notes |
|---------------------|--------------|-------|
| `MinerU:Executable` | `mineru-kit` | Full path to the MinerU CLI, or a command name on `PATH` |
| `MinerU:Tier`       | `basic`      | `flash`, `basic`, `standard` or `advanced`. `standard` crashed on an 8 GB GPU in testing. |

Measured on an RTX 4060 Ti (8 GB) with the `basic` tier: about 0.3 pages/s once the models are loaded, with peak VRAM around 3.5 GB. A 500-page book takes roughly 30 minutes of OCR.

### Run

```bash
dotnet run --launch-profile http
```

The API listens on `http://localhost:5082`. With the `https` profile it uses `https://localhost:7109`. In Development, the interactive API reference is at `http://localhost:5082/scalar`, and the link is also printed in the log at startup.

### Try it

```bash
# Upload a PDF
curl -F "file=@textbook.pdf" http://localhost:5082/Document
# → 202 Accepted  {"jobId":"3f2c..."}

# Check progress
curl http://localhost:5082/Document/status
# → {"state":"Ocr","fileName":"textbook.pdf","progress":null,"error":null}
```

When the job reaches `Ready`, the Markdown is in `data/markdown/<hash>/<hash>.md`. If the job fails, `error` contains the reason. For OCR failures, that's the end of MinerU's error output.

## API

| Method | Route              | Description                    | Responses |
|--------|--------------------|--------------------------------|-----------|
| POST   | `/Document`        | Upload a PDF (form field `file`) | `202` with `jobId`; `400` empty / not a PDF / encrypted / corrupt; `409` ingestion busy or queue full |
| GET    | `/Document/status` | Current ingestion snapshot     | `200` |

`state` is one of: `Idle`, `Queued`, `UnloadingLlm`, `Ocr`, `Chunking`, `Embedding`, `Indexing`, `Ready`, `Failed`.

## Roadmap

- [x] OCR to Markdown + LaTeX (MinerU)
- [ ] OCR page progress in the status endpoint
- [ ] Structure-aware chunking (chapters, sections, equations)
- [ ] Embedding generation with a local model
- [ ] Vector indexing in Qdrant, with indexing skipped for books already in the index
- [ ] Overwrite / re-index option for books that were already processed
- [ ] Retrieval + answer-generation endpoint with source citations
- [ ] Unit tests for `PdfValidator` and the ingestion worker
- [ ] Persisted job history instead of a single in-memory status
