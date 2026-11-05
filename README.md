# RAG Service for STEM Textbooks

A self-hosted retrieval-augmented generation (RAG) backend for STEM textbook PDFs, written in C# on ASP.NET Core (.NET 10).

> **Status: working MVP.** Upload, validation, queueing, job-status reporting, **OCR to Markdown + LaTeX (via MinerU)**, **chunking**, **embeddings (Ollama)** and **indexing (Qdrant)** are implemented. Questions are currently answered by `scripts/ask.py`; a question-answering API endpoint is on the roadmap.

## What works today

- **PDF upload API.** `POST /Document` accepts a multipart file upload.
- **Upload validation** (`Services/PdfTools/PdfValidator.cs`):
  - checks the `%PDF-` magic-byte signature without trusting the file extension or content type
  - opens the document with [PdfPig](https://github.com/UglyToad/PdfPig) to reject password-protected and corrupt files
  - rewinds the stream so the caller can still persist the file afterwards
- **Background ingestion.** Valid files are saved to `data/uploads/{jobId}.pdf` and handed to a `BackgroundService` worker through a bounded `System.Threading.Channels` queue. When the job finishes, the upload is moved to `data/markdown/<hash>/source.pdf`, so each book is stored once no matter how often it's uploaded.
- **OCR with [MinerU](https://github.com/opendatalab/MinerU).** The worker runs `mineru-kit` as a child process and converts the PDF to Markdown, with formulas preserved as LaTeX.
- **OCR progress.** MinerU's log is read line by line while it runs. The status shows the page window being processed and an estimate of the time left.
- **Figure extraction.** MinerU embeds figures as base64 images, which made up about 75% of the Markdown. `FigureExtractor` writes each one to `images/fig-0001.jpg`, `fig-0002.jpg`, … and replaces it with a relative link (`![](images/fig-0001.jpg)`), so the text can reference figures by name.
- **Content-hash caching.** Every upload is hashed (SHA-256). The Markdown is stored at `data/markdown/<hash>/<hash>.md`. If that file already exists, the OCR stage is skipped, so re-uploading a book only re-runs the later stages.
- **Chunking.** `MarkdownChunker` splits the Markdown at headings, merges sections under 30 words into the next one and cuts sections over 800 words at paragraph boundaries. Each chunk starts with its heading. The chunks are written to `data/markdown/<hash>/chunks.json`.
- **Embeddings.** Chunks are sent to Ollama (`/api/embed`) in batches of 32, using `qwen3-embedding:0.6b`. The status shows `n/m chunks`.
- **Vector index.** `QdrantIndexer` stores the vectors in the Qdrant collection `chunks` (cosine) with `ContentHash`, `Index`, `Heading` and `Text` as payload. Uploading a book again deletes its old points first, so a book is never indexed twice.
- **GPU hand-over.** The `UnloadingLlm` stage asks Ollama to unload every loaded model before OCR starts, because MinerU and the LLM don't fit on an 8 GB card together.
- **Asking questions.** `scripts/ask.py` embeds the question, takes the 5 closest chunks from Qdrant and sends them with the question to the LLM. It prints the answer and the headings of the chunks it used.
- **Job status.** `GET /Document/status` returns the current state of the ingestion job, with enum values serialized as strings.
- **OpenAPI + Scalar** API reference UI in the Development environment. The link is printed in the log at startup.

## Results

Pipeline timings on an RTX 4060 Ti (8 GB VRAM), all on the same book:

| Stage | Book | Result |
|---|---|---|
| OCR (MinerU, basic tier) | *Thomas' Calculus* (789 pages) | ~12.9 min (~1.0 pages/s), peak VRAM ~2.3 GiB |
| Chunking | *Thomas' Calculus* (789 pages) | 1,322 chunks |
| Embedding + indexing | *Thomas' Calculus* (789 pages) | ~2.5 min |
| Answering a question (thinking off) | *Thomas' Calculus* (789 pages) | 17–42 s (3 questions; the slowest includes loading the LLM) |

Retrieval accuracy, measured on 60 real student questions from Math StackExchange against *Thomas' Calculus* (1,322 chunks). A question counts as answered when a retrieved chunk alone contains the information needed to answer it, as judged by an AI assistant (Devin):

| Retrieved chunks | Full answer | Full or partial answer |
|------------------|-------------|------------------------|
| Top 1            | 31.7%       | 41.7%                  |
| Top 3            | 53.3%       | 70.0%                  |
| **Top 5**        | **63.3%**   | **86.7%**              |

Mean reciprocal rank (full answers): 0.432. Median retrieval time: about 0.2 s per question, with the question embedded on the CPU. Method, per-question verdicts and limitations: [`benchmark/`](benchmark/README.md).

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
        └─────────┬──────────┘
                  ▼
        ┌────────────────────┐
        │  MarkdownChunker   │  headings → chunks, written to chunks.json
        └─────────┬──────────┘
                  ▼
        ┌────────────────────┐
        │    OllamaClient    │  /api/embed, batches of 32 (also unloads the models first)
        └─────────┬──────────┘
                  ▼
        ┌────────────────────┐
        │   QdrantIndexer    │  replaces the book's points in the collection
        └────────────────────┘
```

Asking a question is a separate path that doesn't go through the API:

```
scripts/ask.py "question"
      │  Ollama embeds the question (same model as the chunks)
      ▼
   Qdrant: 5 closest chunks ──▶ prompt = chunks + question ──▶ Ollama LLM ──▶ answer + sources
```

### Data layout

```
data/
├── uploads/            uploads of jobs that are still running
└── markdown/<hash>/    everything known about one book
    ├── <hash>.md       Markdown + LaTeX, figures as ![](images/fig-NNNN.jpg)
    ├── chunks.json     the chunks that were embedded
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
  Chunking/
    MarkdownChunker.cs       Markdown → chunks (split at headings, merged / cut by size)
  Ollama/
    OllamaClient.cs          embeddings + unloading the loaded models
    OllamaOptions.cs         "Ollama" configuration section
  Indexing/
    QdrantIndexer.cs         creates the collection, replaces and upserts a book's points
    QdrantOptions.cs         "Qdrant" configuration section
  LocalPaths/
    DocumentPaths.cs         single place for all data/ paths (uploads, Markdown, chunks, images, source PDF)
scripts/
  ask.py                     question → Qdrant → LLM
benchmark/
  README.md                  how the retrieval benchmark was run and judged
  judgments.json             60 questions, retrieved sections and verdicts
compose.yaml                 Qdrant
Program.cs                   DI registration and middleware
```

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Python 3.12 and MinerU (see below)
- [Docker](https://docs.docker.com/get-docker/), for Qdrant
- [Ollama](https://ollama.com), for the embeddings and the LLM
- [uv](https://docs.astral.sh/uv/), to run `scripts/ask.py`
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

Measured on an RTX 4060 Ti (8 GB) with the `basic` tier: *Thomas' Calculus* (789 pages) took about 12.9 minutes (~1.0 pages/s, model loading included), with peak VRAM around 2.3 GiB (2,391 MiB above the idle GPU, sampled every 2 s with `nvidia-smi`). Formula-heavy pages are slower, and very short PDFs look slow because loading the models takes about 30 seconds.

### Qdrant and Ollama setup

```bash
docker compose up -d                 # Qdrant: REST + dashboard on 6333, gRPC on 6334
ollama pull qwen3-embedding:0.6b     # embeddings, 639 MB
ollama pull qwen3.5                  # the LLM that answers
```

| Key                     | Default                  | Notes |
|-------------------------|--------------------------|-------|
| `Ollama:Url`            | `http://localhost:11434` | |
| `Ollama:EmbeddingModel` | `qwen3-embedding:0.6b`   | A different model has a different vector size. Use a new `Qdrant:Collection` and change the model in `scripts/ask.py` too. |
| `Qdrant:Host`           | `localhost`              | |
| `Qdrant:Port`           | `6334`                   | gRPC port |
| `Qdrant:Collection`     | `chunks`                 | `scripts/ask.py` reads from this collection |

Embedding and indexing 1322 chunks took about 2.5 minutes on an RTX 4060 Ti.

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
```

When the job reaches `Ready`, the Markdown is in `data/markdown/<hash>/<hash>.md`, its figures are in `data/markdown/<hash>/images/` and the chunks are indexed in Qdrant (dashboard: `http://localhost:6333/dashboard`). If the job fails, `error` contains the reason. For OCR failures, that's the end of MinerU's error output.

Then ask a question:

```bash
uv run scripts/ask.py "What is the Maclaurin series of cos x?"
```

```
Based on **TABLE 10.1** in the provided text (specifically under "Frequently used Taylor series"), the formula for $\cos x$ expanded at $x=0$ is given as follows:

$$ \cos x = 1 - \frac{x^{2}}{2!} + \frac{x^{4}}{4!} - \cdots + (-1)^{n}\frac{x^{2n}}{(2n)!} + \cdots $$

This can also be expressed in summation notation as:
$$ \cos x = \sum_{n=0}^{\infty}\frac{(-1)^{n}x^{2n}}{(2n)!}, \quad |x| < \infty $$

Sources:
- Finding Taylor Series at x = 0 (Maclaurin Series)
- Maclaurin Series
- TABLE 10.1 Frequently used Taylor series
- Exercises 10.9 > Finding Taylor Series
- Finding Taylor and Maclaurin Series
```

`uv` installs the script's two dependencies (`ollama`, `qdrant-client`) on the first run. The script sets three options on purpose:

- The question is embedded on the CPU (`num_gpu: 0`). On an 8 GB card the embedding model next to the LLM pushed the LLM out of VRAM in testing, and Ollama had to reload it (about 20 s).
- `num_ctx` is 16384, so that the retrieved chunks fit. Five chunks were about 5,600 tokens in one test.
- Thinking is off (`think=False`). With it on, one answer took about 2.5 minutes in testing, against 30–45 s with it off.

Don't ask questions while an ingestion job is running. The GPU is shared, and the job unloads the LLM when it starts.

## Retrieval benchmark

On 60 real questions from Math StackExchange, a retrieved top-5 chunk contains what is needed to answer the question **63.3%** of the time (top 1: 31.7%, top 3: 53.3%), and **86.7%** if partial answers count. The corpus is *Thomas' Calculus* (789 pages, 1322 chunks).

Each retrieved chunk was read and judged by an AI assistant (Devin) for whether it alone contains the information needed to answer the question. This tests retrieval only, not the LLM's answers. The questions, the retrieved sections, every verdict with a reason, the full method and the limitations (single AI judge, not fully human-reviewed) are in [`benchmark/`](benchmark/README.md). The main weakness it found is that exercise-list chunks often outrank the explanations.

## API

| Method | Route              | Description                    | Responses |
|--------|--------------------|--------------------------------|-----------|
| POST   | `/Document`        | Upload a PDF (form field `file`) | `202` with `jobId`; `400` empty / not a PDF / encrypted / corrupt; `409` ingestion busy or queue full |
| GET    | `/Document/status` | Current ingestion snapshot     | `200` |

`state` is one of: `Idle`, `Queued`, `UnloadingLlm`, `Ocr`, `Chunking`, `Embedding`, `Indexing`, `Ready`, `Failed`.

## Roadmap

- [ ] Structure-aware chunking (chapters, sections, equations, figure links)
- [ ] Skip embedding for books that are already in the index
- [ ] Overwrite / re-index option for books that were already processed
- [ ] The same as an API endpoint, refusing requests while an ingestion job runs
- [ ] Better retrieval on long sections (smaller chunks, reranking): chunks of up to 800 words can rank below short, generic ones such as exercise lists
- [ ] Optional figure descriptions with a vision model (e.g. Qwen3.5), written into the image alt text
- [ ] Unit tests for `PdfValidator` and the ingestion worker
- [ ] Persisted job history instead of a single in-memory status

### Ideas for later

Nothing planned yet, just things I want to look into someday:

- **Let the LLM query the RAG itself.** What a user types is often vague or badly worded, so searching with the raw question gives weak results. Instead the LLM could get a search tool (function calling or an MCP server) and use it in a loop: rewrite the query, read the results, search again if they don't answer the question, and check the chunks before it replies. I think this is viable, but I haven't tried it.
