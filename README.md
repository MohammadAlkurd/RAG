# RAG Service for STEM Textbooks

A self-hosted retrieval-augmented generation (RAG) backend for STEM textbook PDFs, written in C# on ASP.NET Core (.NET 10).

> **Status: early development.** Upload, validation, queueing, job-status reporting and **OCR to Markdown + LaTeX (via MinerU)** work today. The chunking, embedding and indexing stages are still **placeholders** that only move the job through its states. Retrieval and question answering haven't been started.

## What works today

- **PDF upload API.** `POST /Document` accepts a multipart file upload.
- **Upload validation** (`Services/PdfTools/PdfValidator.cs`):
  - checks the `%PDF-` magic-byte signature without trusting the file extension or content type
  - opens the document with [PdfPig](https://github.com/UglyToad/PdfPig) to reject password-protected and corrupt files
  - rewinds the stream so the caller can still persist the file afterwards
- **Background ingestion.** Valid files are saved to `data/uploads/{jobId}.pdf` and handed to a `BackgroundService` worker through a bounded `System.Threading.Channels` queue. When the job finishes, the upload is moved to `data/markdown/<hash>/source.pdf`, so each book is stored once no matter how often it's uploaded.
- **OCR with [MinerU](https://github.com/opendatalab/MinerU).** The worker runs `mineru-kit` as a child process and converts the PDF to Markdown, with formulas preserved as LaTeX.
- **OCR progress.** MinerU's log is read line by line while it runs. The status shows the page window being processed and an estimate of the time left, e.g. `pages 65-128/347, ~3 min left`.
- **Figure extraction.** MinerU embeds figures as base64 images, which made up about 75% of the Markdown. `FigureExtractor` writes each one to `images/fig-0001.jpg`, `fig-0002.jpg`, … and replaces it with a relative link (`![](images/fig-0001.jpg)`), so the text can reference figures by name.
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
        │  IDocumentParser   │  MinerUParser → runs mineru-kit, reports page progress
        └─────────┬──────────┘
                  ▼
        ┌────────────────────┐
        │  FigureExtractor   │  base64 images → images/fig-NNNN.jpg + links
        └────────────────────┘
```

### Data layout

```
data/
├── uploads/            uploads of jobs that are still running
└── markdown/<hash>/    everything known about one book
    ├── <hash>.md       Markdown + LaTeX, figures as ![](images/fig-NNNN.jpg)
    ├── source.pdf      the original PDF, kept once
    └── images/
        ├── fig-0001.jpg
        └── ...
```

### Ingestion state machine

`IngestionState` defines how a job moves through the pipeline:

```
Idle → Queued → UnloadingLlm → Ocr → Chunking → Embedding → Indexing → Ready
                                     (any stage) ───────────────────▶ Failed
```

Every job goes through all stages. A stage is skipped when its output already exists (currently only `Ocr`, through the Markdown cache). Figure extraction runs at the end of the `Ocr` stage even when OCR was cached. It does nothing on a file that's already clean.

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
    FigureExtractor.cs       moves embedded images out of the Markdown
  LocalPaths/
    DocumentPaths.cs         single place for all data/ paths (uploads, Markdown, images, source PDF)
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

Measured on an RTX 4060 Ti (8 GB) with the `basic` tier: a 347-page textbook took about 4.5 minutes (~1.3 pages/s, model loading included), with peak VRAM around 3.5 GB. Formula-heavy pages are slower, and very short PDFs look slow because loading the models takes about 30 seconds.

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
# → {"state":"Ocr","fileName":"textbook.pdf","progress":"pages 65-128/347, ~3 min left","error":null}
```

When the job reaches `Ready`, the Markdown is in `data/markdown/<hash>/<hash>.md` and its figures are in `data/markdown/<hash>/images/`. If the job fails, `error` contains the reason. For OCR failures, that's the end of MinerU's error output.

## API

| Method | Route              | Description                    | Responses |
|--------|--------------------|--------------------------------|-----------|
| POST   | `/Document`        | Upload a PDF (form field `file`) | `202` with `jobId`; `400` empty / not a PDF / encrypted / corrupt; `409` ingestion busy or queue full |
| GET    | `/Document/status` | Current ingestion snapshot     | `200` |

`state` is one of: `Idle`, `Queued`, `UnloadingLlm`, `Ocr`, `Chunking`, `Embedding`, `Indexing`, `Ready`, `Failed`.

## Roadmap

- [x] OCR to Markdown + LaTeX (MinerU)
- [x] OCR page progress in the status endpoint
- [x] Extract embedded figures into image files
- [ ] Structure-aware chunking (chapters, sections, equations, figure links)
- [ ] Embedding generation with a local model
- [ ] Vector indexing in Qdrant, with indexing skipped for books already in the index
- [ ] Overwrite / re-index option for books that were already processed
- [ ] Retrieval + answer-generation endpoint with source citations
- [ ] Optional figure descriptions with a vision model (e.g. Qwen3.5), written into the image alt text
- [ ] Unit tests for `PdfValidator` and the ingestion worker
- [ ] Persisted job history instead of a single in-memory status
