# RAG Service for STEM Textbooks

A self-hosted retrieval-augmented generation (RAG) backend for STEM textbook PDFs, written in C# on ASP.NET Core (.NET 10).

> **Status: early development.** Upload, validation, queueing and job-status reporting work today. The ingestion stages (OCR, chunking, embedding, indexing) are **placeholders** that only move the job through its states. They don't process the document yet. Retrieval and question answering haven't been started.

## What works today

- **PDF upload API.** `POST /Document` accepts a multipart file upload.
- **Upload validation** (`Services/PdfTools/PdfValidator.cs`):
  - checks the `%PDF-` magic-byte signature without trusting the file extension or content type
  - opens the document with [PdfPig](https://github.com/UglyToad/PdfPig) to reject password-protected and corrupt files
  - rewinds the stream so the caller can still persist the file afterwards
- **Background ingestion.** Valid files are saved to `data/uploads/{jobId}.pdf` and handed to a `BackgroundService` worker through a bounded `System.Threading.Channels` queue.
- **Job status.** `GET /Document/status` returns the current state of the ingestion job.
- **OpenAPI + Scalar** API reference UI in the Development environment.

## Architecture

```
            POST /Document
                  │
                  ▼
        ┌───────────────────┐   invalid → 400
        │ DocumentController│── busy    → 409
        └─────────┬─────────┘
                  │ PdfValidator.Check
                  │ save to data/uploads/
                  ▼
        ┌───────────────────┐
        │  IngestionQueue   │  bounded Channel<IngestionJob>, capacity 1
        └─────────┬─────────┘
                  ▼
        ┌────────────────────┐        ┌─────────────────────┐
        │  IngestionWorker   │──────▶ │   IngestionStatus   │ ◀── GET /Document/status
        │ (BackgroundService)│ writes │ (immutable snapshot)│
        └────────────────────┘        └─────────────────────┘
```

### Ingestion state machine

`IngestionState` defines how a job moves through the pipeline:

```
Idle → Queued → UnloadingLlm → Ocr → Chunking → Embedding → Indexing → Ready
                                     (any stage) ───────────────────▶ Failed
```

`IngestionStatus` stores the current state as an immutable `IngestionSnapshot` record behind a `volatile` field. The worker swaps in a new snapshot on each transition, so readers never see a half-updated status. The service handles one document at a time. While a job is running, new uploads get `409 Conflict`.

## Project layout

```
Controllers/
  DocumentController.cs      upload + status endpoints
Services/
  PdfTools/
    PdfValidator.cs          signature, encryption and corruption checks
  Ingestion/
    IngestionJob.cs          job record (id, original name, stored path)
    IngestionQueue.cs        bounded channel wrapper
    IngestionWorker.cs       BackgroundService that runs the pipeline
    IngestionStatus.cs       thread-safe current-status holder
    IngestionSnapshot.cs     immutable status record
    IngestionState.cs        pipeline states
Program.cs                   DI registration and middleware
```

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Run

```bash
dotnet run --launch-profile http
```

The API listens on `http://localhost:5082`. With the `https` profile it uses `https://localhost:7109`. In Development, the interactive API reference is at `http://localhost:5082/scalar`.

### Try it

```bash
# Upload a PDF
curl -F "file=@textbook.pdf" http://localhost:5082/Document
# → 202 Accepted  {"jobId":"3f2c..."}

# Check progress
curl http://localhost:5082/Document/status
# → {"state":3,"fileName":"textbook.pdf","progress":null,"error":null}
```

`state` is serialized as the numeric value of `IngestionState` (`3` = `Ocr`).

## API

| Method | Route              | Description                    | Responses |
|--------|--------------------|--------------------------------|-----------|
| POST   | `/Document`        | Upload a PDF (form field `file`) | `202` with `jobId`; `400` empty / not a PDF / encrypted / corrupt; `409` ingestion busy or queue full |
| GET    | `/Document/status` | Current ingestion snapshot     | `200` |

## Roadmap

- [ ] OCR for scanned pages and math-heavy content
- [ ] Structure-aware chunking (chapters, sections, equations)
- [ ] Embedding generation with a local model
- [ ] Vector indexing in Qdrant
- [ ] Retrieval + answer-generation endpoint with source citations
- [ ] Unit tests for `PdfValidator` and the ingestion worker
- [ ] Persisted job history instead of a single in-memory status
