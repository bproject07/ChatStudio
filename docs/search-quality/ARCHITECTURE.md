# Architecture and Data Flow

[Repository Root](../../README.md) · [Chat Studio Overview](../chat-studio/README.md) · [Chat Architecture](../chat-studio/ARCHITECTURE.md) · [Chat Operations](../chat-studio/OPERATIONS.md)

## Purpose

The `SearchQuality` assembly provides the shared document indexing and retrieval engine used by both Search Quality Studio and Chat Studio. In laboratory mode, it separates three distinct concerns:

1. **Retrieval** — which text chunks are found and in what order;
2. **Relevance gating** — which retrieved chunks are actually useful for answering the question;
3. **Generation** — how the model composes an answer using only the admitted context.

This separation is important: a high technical similarity score does not guarantee that a chunk contains the answer, and a fluent model response does not prove that the retrieval method ranked the sources properly.

## Core Components

| Project or Area | Responsibility |
|---|---|
| `SearchQuality` assembly | Shared domain models, application orchestration, corpus processing, PostgreSQL/OpenAI adapters, benchmarking, and reporting |
| `SearchQuality.Chat` | Multi-agent conversational Web/Telegram application using `SearchQuality` for long-term hybrid conversation memory |
| `SearchQuality.Web` / `SearchQuality.Cli` | Laboratory Web/API and CLI hosts (not included in this standalone repository) for retrieval benchmarking, indexing, and diagnostics |
| PostgreSQL | Document and chunk storage (`documents`/`chunks`), GIN Full-Text index, and HNSW `pgvector` index |
| OpenAI-compatible services | Embeddings (`text-embedding-3-large`), local RAG/chat generation, interactive Judge, and managed File Search |

`Application`, `Corpus`, `Postgres`, `OpenAI`, `Benchmarking`, and `Reporting` are folders inside a single `SearchQuality` project, not separate assemblies. Chat Studio references this shared project while isolating its runtime flow and tests in `ChatStudio.slnx`; the laboratory Judge/benchmark gate is not automatically applied to chat turns.

## Document Lifecycle

### 1. Local Ingestion

Supported formats are `.md`, `.txt`, `.pdf`, and `.docx`. The ingestion pipeline extracts text, normalizes blank lines and control characters, and determines a title from document metadata or the first suitable heading.

Scanned PDFs without a text layer are not processed via OCR and may be rejected as empty.

Uploading a file modifies `data/documents/` only; it does not automatically update PostgreSQL or OpenAI File Search. (In Chat Studio, compacted conversation summaries are written to `data/chat-history/<userId>/` and indexed immediately into the `chat_lab` PostgreSQL schema.)

### 2. Chunking

For PostgreSQL indexes, each document is split into overlapping word-based chunks controlled by:

- `Benchmark:ChunkSizeWords`;
- `Benchmark:ChunkOverlapWords`.

Overlap preserves context across chunk boundaries, at the cost of additional embeddings and potentially returning multiple adjacent chunks from the same document.

`Top K` always denotes the number of chunks, not the number of whole documents.

### 3. PostgreSQL Index

The PostgreSQL schema contains two related tables:

```text
documents (1) ───────────< (N) chunks
    id PK                    id PK
    title                    document_id FK
    file_name                document_title
    content                  ordinal
                             content
                             embedding
                             search_vector
```

`document_title` in `chunks` is a denormalized copy for fast retrieval and display. `documents.title` remains the canonical document title. (In Chat Studio's `chat_lab` schema, both `documents` and `chunks` also carry `user_id` so every retrieval query filters strictly by user before ranking.)

On `chunks`, two indexes are created:

- a **GIN index** over the generated `tsvector` (`search_vector`) for Full-Text search;
- an **HNSW index** using `vector_cosine_ops` over `embedding` for `pgvector` similarity search.

Full re-indexing replaces the contents of both tables inside a transaction and computes an embedding for every chunk. Synchronization is the incremental variant: `documents.content_hash` stores a hash of the content together with the chunking parameters and embedding model name, so only added, modified, or deleted documents are processed (changing chunking or embedding settings invalidates all hashes and triggers a full rebuild). Both paths produce an identical final index state.

### 4. OpenAI File Search Index

File Search uses a separate OpenAI Vector Store. The application uploads a normalized text copy of each document and configures token-based chunking parameters (`Benchmark:FileSearchChunkSizeTokens` and `Benchmark:FileSearchChunkOverlapTokens`). A local state file tracks the document-to-uploaded-file mapping (including content hashes) so incremental sync uploads only new or modified files and removes deleted ones without recreating the Vector Store; uploaded files expire after 7 days (orphan protection) and files nearing expiration are refreshed during sync. "Rebuild" creates a fresh Vector Store from scratch.

This index is separate from the PostgreSQL index. When local files change, the two indexes are updated via separate operations.

## Interactive PostgreSQL Search

When Full-Text, `pgvector`, and/or Hybrid are selected in interactive laboratory search, the flow is:

```text
1. Single question
2. Each selected method retrieves its own Top K candidates
3. Unique candidate chunks across methods are evaluated once by the Judge
4. Relevance scores are mapped back to each method's candidates
5. Chunks below the minimum threshold are rejected
6. Each method generates a separate GPT answer using only its approved chunks
```

The minimum interactive threshold is `40%` AI relevance (exact `40%` is accepted). If no chunk passes the threshold:

- the GPT generator is not called;
- GPT tokens for that method are `0`;
- the result explicitly states that no sufficiently relevant source was found.

The final number of sources after filtering may be smaller than the requested `Top K`.

*(Note: Chat Studio does not run the laboratory Judge gate before answering; it passes the retrieved `Top K` archive snippets directly as conversation context.)*

## Retrieval Methods

### PostgreSQL Full-Text (`postgres-full-text`)

The user query is converted into a PostgreSQL `tsquery`:

- common Bulgarian stop words are removed;
- up to 20 distinct normalized terms are retained;
- `OR` and prefix (`:*`) conditions are constructed;
- `ts_rank_cd` ranks matching chunks.

The GIN index accelerates matching. Raw `ts_rank_cd` has no fixed upper bound and is used purely for ordering.

Full-Text may return fewer than `Top K` chunks—including zero—when there is no lexical overlap.

### PostgreSQL `pgvector` (`postgres-pgvector`)

The embedding model converts the query into a vector. PostgreSQL compares this vector against stored chunk embeddings and orders results by cosine distance (`<=>`).

The reported raw score is:

```text
cosine similarity = 1 - cosine distance
```

This is geometric similarity, not a calibrated probability of a correct answer. As long as enough chunks are indexed, a nearest-neighbor query always returns the full `Top K`, even when all candidates are weak.

### PostgreSQL Hybrid with RRF (`postgres-hybrid`)

Hybrid executes both Full-Text and `pgvector` queries and merges their rankings via **Reciprocal Rank Fusion (RRF)**:

```text
RRF score = 1 / (60 + lexical_rank)
          + 1 / (60 + vector_rank)
```

The method never adds `ts_rank_cd` and cosine similarity directly because they live on incompatible scales; instead, it fuses their ordinal rank positions. A wider candidate pool (`max(TopK * 4, 20)`) is fetched from each leg before RRF selects the final `Top K`.

Raw RRF is a ranking score, not a percentage of relevance.

### OpenAI File Search

The Responses API invokes the `file_search` tool over the configured Vector Store. OpenAI manages retrieval and generation in a single call and returns the retrieved chunks, provider score, and generated answer.

In interactive mode, the local Judge can still evaluate and filter displayed chunks after the managed File Search call completes. Consequently, the PostgreSQL pre-generation threshold and the File Search post-generation filter are not identical experiments. If the Judge call itself fails, the File Search answer is suppressed so the UI never cites sources hidden from the source list.

File Search does not separate retrieval latency from generation latency: the entire call is measured as generation time, and retrieval-only comparisons mark it as not applicable.

## AI Relevance Evaluation

The Judge model receives the question and all unique `Top K` candidates in a single structured request. For each chunk it returns:

- a relevance score from `0` to `100`;
- a brief justification.

The scale evaluates whether the excerpt itself contains evidence for answering the question, not merely whether it shares keywords with the query. It is an approximate LLM judgment, not a calibrated mathematical probability, and does not replace a ground-truth benchmark with known expected documents.

## Answer Generation

For local PostgreSQL strategies, the generator receives:

- the question;
- only the approved chunks for that specific method;
- the source identifier and title for each chunk;
- instructions not to use external knowledge and to cite the provided documents.

Each selected method generates its own independent answer. Selecting three methods with `Top K = 5` produces up to three separate generation requests with up to five approved chunks each, not one request with 15 chunks.

## Tokens and Latency

Metrics are tracked separately for:

- generator input/output tokens for the answer;
- query embedding tokens for `pgvector` and Hybrid;
- shared Judge request tokens;
- total retrieval and generation latency.

Full-Text uses no query embeddings. File Search does not expose a separate count for internal managed retrieval embeddings.

Because Judge evaluation runs once across unique candidate chunks, its cost is not artificially multiplied across each PostgreSQL method in side-by-side comparisons.

## OpenAI Client Resilience

- Transient HTTP errors (`429`, `500`, `502`, `503`, `504`) and network interruptions are retried automatically up to 4 attempts with exponential backoff, honoring the API's `Retry-After` header.
- Structured `error.message` details from OpenAI/LiteLLM are included in exceptions for diagnostics; raw response bodies are never echoed.
- Responses with status `incomplete` (typically caused by exhausting `max_output_tokens`, including reasoning tokens) are reported explicitly rather than appearing as blank answers.
- Embedding requests are batched automatically to stay within API limits on larger corpora.

## Consistency and Safety

- Both Web applications accept only loopback requests by default and validate the `Host` header (`localhost`, `127.0.0.1`, `::1`), preventing DNS rebinding when running locally without authentication.
- API keys and connection strings are never returned to the browser.
- Deleting a local corpus file does not automatically remove its old chunks from PostgreSQL or OpenAI File Search until a sync or rebuild operation is run.
