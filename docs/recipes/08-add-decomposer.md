# Recipe 08: Add a Decomposer

Intent: add a new decomposer (e.g., `Decomposer<MarkdownSource>`, `Decomposer<ChessPgnSource>`, `Decomposer<DnaFastaSource>`) that produces substrate records from a content source.

Decomposers are **thin source parsers**. They identify content from their source format, hand it to the central ingestion pipeline by modality, and attach metadata edges over the canonical hashes the pipeline returns. They do NOT canonicalize, hash, batch, or manage transactions — the pipeline does all of that.

---

## The architecture in one diagram

```
                         CLIENT-SIDE                                      DB
                         (decomposer process)                             (server)
─────────────────────────────────────────────────────────────────   ─────────────────
[Source File]
   │
   ▼
Decomposer<TSource>     ← per-source orchestrator
   │  reads records, identifies content + provenance
   │
   │  For each record, calls shared canonicalizers (also client-side):
   ▼
ITextCanonicalizer / IAudioCanonicalizer / IImageCanonicalizer / ...
   │  client-side; calls libhartonomous via IComputeFacade for:
   │     - BLAKE3 hashing of every atom
   │     - NFC normalization (text), dtype decode (safetensors), etc.
   │     - Merkle DAG construction for compositions
   │     - geometric primitives (Super-Fibonacci S³, Hilbert, GSO)
   │  emits Entity / Edge / Sequence / Physicality records into the batch
   │  with hashes and geometry already populated
   ▼
IngestionBatch (in-memory accumulator on the client)
   │
   ▼
IIngestionPipeline.SubmitAsync(batch)
   │  client-side: routes records to the right partition, opens transaction,
   │  issues set-based bulk INSERTs (INSERT ... SELECT FROM unnest(...) ON CONFLICT DO NOTHING),
   │  commits.
   │
   │  Wire format: typed arrays. NO row-by-row. NO server-side compute.
   ▼─────────────────────────────────────────────────────────────────▶ [DB]
                                                                       set-based INSERT
                                                                       UNIQUE constraint dedupes
                                                                       returns to client
```

Three layers, three jobs, no overlap:

| Layer | Job | Tools |
|---|---|---|
| **Decomposer + canonicalizers** (client) | Read source; canonicalize via shared services; emit pre-computed records | `IComputeFacade`, `ITextCanonicalizer`, `IAudioCanonicalizer`, ... |
| **Pipeline** (client → DB) | Batch records; bulk-insert via set-based SQL; manage transactions | `Npgsql`, `NpgsqlBinaryImporter` for COPY paths |
| **DB** (server) | Receive arrays; INSERT; UNIQUE constraints dedupe | Set-based SQL only — NO loops, cursors, recursion, RBAR |

Convergence (same content → same hash from any decomposer) happens at the **shared canonicalizers**, not at the DB. Two decomposers calling `_text.Canonicalize("Hello.")` get the same root hash because both routes go through the same libhartonomous primitives.

---

## Prerequisites

- `TSource` marker type defined in `src/Hartonomous.Core/Decomposition/Sources/{Pascal}Source.cs` implementing `ISourceFormat`.
- The decomposer's `ProvenanceCode` exists in `substrate.provenance` (recipe `07-add-provenance-class.md`).
- Any new entity types, edge types, junctions, physicality types this decomposer requires (recipes `02`–`06`).
- For NEW modalities only: a new pipeline canonicalizer (`ICanonicalizer<TModality>`) — that's a separate, larger change. Most decomposers reuse existing modality canonicalizers (text, image, audio, video, safetensors).

---

## Steps

### 1. Declare the source marker type

`src/Hartonomous.Core/Decomposition/Sources/{Pascal}Source.cs`:

```csharp
namespace Hartonomous.Core.Decomposition.Sources;

public sealed class {Pascal}Source : ISourceFormat { }
```

One file. No methods. The marker exists so the type system can identify the decomposer.

### 2. Implement the reader

`src/Hartonomous.Decomposers/{Pascal}/{Pascal}Reader.cs` — parses the source format into in-memory records. No DB access. No hashing. No pipeline calls.

```csharp
namespace Hartonomous.Decomposers.{Pascal};

public sealed class {Pascal}Reader
{
    private readonly {Pascal}Config _config;

    public {Pascal}Reader({Pascal}Config config) => _config = config;

    public async IAsyncEnumerable<{Pascal}Record> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Open files / streams from _config.Source
        // Yield {Pascal}Record instances
    }

    public int TotalCount { get; private set; }
}
```

`{Pascal}Record` is a simple record holding the source's natural fields (e.g., for Tatoeba: `(long Id, string Text, byte[]? Audio, int LanguageId, IReadOnlyList<TranslationRef> Translations)`). The record is what the decomposer iterates over.

### 3. Implement the decomposer

`src/Hartonomous.Decomposers/{Pascal}/{Pascal}Decomposer.cs`:

```csharp
namespace Hartonomous.Decomposers.{Pascal};

public sealed class {Pascal}Decomposer : Decomposer<{Pascal}Source>
{
    private readonly IIngestionPipeline   _pipeline;
    private readonly ITextCanonicalizer   _text;     // client-side; uses libhartonomous via IComputeFacade
    private readonly IAudioCanonicalizer  _audio;    // client-side; uses libhartonomous via IComputeFacade
    // ... other modality canonicalizers your source needs
    private readonly {Pascal}Reader       _reader;
    private readonly ILogger<{Pascal}Decomposer> _logger;

    public {Pascal}Decomposer(
        IIngestionPipeline pipeline,
        ITextCanonicalizer text,
        IAudioCanonicalizer audio,
        {Pascal}Reader reader,
        ILogger<{Pascal}Decomposer> logger)
    {
        _pipeline = pipeline;
        _text = text;
        _audio = audio;
        _reader = reader;
        _logger = logger;
    }

    public override string ProvenanceCode => "{provenance_code}";
    public override IReadOnlyList<Phase> Phases => [Phase.{PhaseEnum}];

    public override async Task ValidateSourceAsync(CancellationToken ct)
    {
        // Verify input files exist, are parseable, have the expected structure.
        // Throw with descriptive message on any failure.
    }

    public override async Task DecomposeAsync(IProgressReporter reporter, CancellationToken ct)
    {
        var batch = new IngestionBatch();

        await foreach (var record in _reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            // 1. Client-side canonicalization. Hashes are computed in libhartonomous (via the facade).
            //    Records (Entity / Sequence / Physicality) for the canonicalized DAG are added to `batch`.
            //    Returns the root hash — same content → same hash from any decomposer.
            var textHash = _text.Canonicalize(record.Text, ProvenanceCode.{ProvenanceEnum}, batch);

            // 2. Add your decomposer's actual contribution — metadata edges and junctions
            //    over the canonical hashes. These are the bookkeeping rows that say
            //    "this content came from this source, in this language, recorded by X."
            batch.AddJunction(JunctionCode.EntityLanguage, textHash, record.LanguageId);

            foreach (var related in record.RelatedRefs)
            {
                var relatedHash = _text.Canonicalize(related.Text, ProvenanceCode.{ProvenanceEnum}, batch);
                batch.AddEdge(EdgeTypeCode.{Relation}, source: textHash, target: relatedHash);
            }

            // 3. Submit when the batch reaches optimal size. The pipeline routes records
            //    to the right partition, opens one transaction, issues bulk set-based INSERTs.
            if (batch.RecordCount >= _pipeline.OptimalBatchSize)
            {
                await _pipeline.SubmitAsync(batch, ct);
                batch = new IngestionBatch();
            }

            reporter.ReportProgress(record.Id, _reader.TotalCount);
        }

        if (batch.RecordCount > 0) await _pipeline.SubmitAsync(batch, ct);
        await _pipeline.FlushAsync(ct);
        reporter.ReportPhaseComplete();
    }
}
```

**What the decomposer DOES**:
- Parse its source format (the reader does this).
- Call shared client-side canonicalizers for any content (text/image/audio/video/safetensors).
- Build IngestionBatch with hashes already populated (computed by libhartonomous via the facade).
- Add its metadata edges and junctions to the batch.
- Hand the batch to the pipeline when full.

**What the decomposer does NOT do**:
- Reinvent hashing — uses `IComputeFacade.Common.Blake3.Hash` (via canonicalizers) which calls libhartonomous. Never builds its own BLAKE3 or Merkle code.
- Reinvent canonicalization — uses the shared `ITextCanonicalizer` etc. so two decomposers with the same content produce the same hash.
- Open `NpgsqlConnection` — pipeline does that.
- Manage transactions — pipeline does that.
- Issue per-row INSERTs — pipeline batches and bulk-INSERTs.
- Parallelize — pipeline does that.
- Run any compute on the DB side — all compute is client-side via libhartonomous.

If you find yourself reaching for `Blake3.Hash` or hand-rolling a Merkle tree, you should be calling a shared canonicalizer instead. If you find yourself touching `NpgsqlConnection`, you've drifted into pipeline territory.

### 4. Register in DI

`src/Hartonomous.Engine/Orchestration/DecomposerRegistration.cs`:

```csharp
services.AddSingleton<{Pascal}Decomposer>();
services.AddSingleton<IDecomposer, {Pascal}Decomposer>(sp => sp.GetRequiredService<{Pascal}Decomposer>());
services.AddSingleton<{Pascal}Reader>();
services.AddSingleton<{Pascal}Config>();
```

### 5. Add a CLI command

See recipe `18-add-cli-command.md`. Minimal version wires the decomposer into a phase invocation.

### 6. Add a PowerShell entrypoint

`scripts/seed/{Pascal}.ps1`:

```powershell
param(
    [string]$Path,
    [string]$ConnectionString = $env:HARTONOMOUS_DB
)
& dotnet run --project (Join-Path $PSScriptRoot '../../src/Hartonomous.Cli') -- ingest-{kebab-code} --path $Path --connection-string $ConnectionString
```

If the new decomposer should run in `seed/All.ps1`, add it in the dependency-ordered list.

### 7. Add tests

`tests/Hartonomous.Decomposers.Tests/{Pascal}/{Pascal}DecomposerTests.cs` — unit tests with a hand-written fake `IIngestionPipeline`. Assert the decomposer submits the expected content, edges, and junctions for known input.

`tests/Hartonomous.Integration.Tests/{Pascal}IntegrationTests.cs` — end-to-end against a real PostgreSQL container. Ingests a small fixture, asserts substrate state.

See recipe `17-add-test.md`.

### 8. Document

- `docs/specs/decomposers/{kebab-code}.md` — full spec (source format, what edges/junctions this decomposer attaches, expected volume).
- `docs/index.md` — register the doc.

### 9. Run and verify

```pwsh
pwsh scripts/build/Dotnet.ps1
pwsh scripts/test/Dotnet.ps1 -Filter {Pascal}DecomposerTests
pwsh scripts/test/Integration.ps1 -Filter {Pascal}IntegrationTests
pwsh scripts/seed/{Pascal}.ps1 -Path tests/.../fixture
```

---

## Cross-modality decomposers — the composition pattern

Some decomposers handle composite sources (e.g., a video file is image frames + audio + alignment). The pattern: submit each component to the pipeline by modality, then attach cross-modal alignment edges.

```csharp
public sealed class VideoDecomposer : Decomposer<VideoSource>
{
    private readonly IIngestionPipeline _pipeline;
    private readonly VideoReader _reader;

    public override async Task DecomposeAsync(IProgressReporter reporter, CancellationToken ct)
    {
        await foreach (var video in _reader.ReadAsync(ct))
        {
            // Submit each frame as image content.
            var frameHashes = new List<EntityHash>(video.FrameCount);
            await foreach (var frame in video.FramesAsync(ct))
            {
                var h = await _pipeline.SubmitContentAsync(
                    frame.Bytes, ModalityCode.Image, ProvenanceCode.{Pascal}, ct);
                frameHashes.Add(h);
            }

            // Submit audio track.
            var audioHash = await _pipeline.SubmitContentAsync(
                video.AudioBytes, ModalityCode.Audio, ProvenanceCode.{Pascal}, ct);

            // Submit the video composition. Pipeline composes a video entity
            // referencing the frame hashes and the audio hash via sequence rows.
            var videoHash = await _pipeline.SubmitCompositionAsync(
                kind: CompositionKind.Video,
                componentHashes: frameHashes.Append(audioHash).ToArray(),
                provenance: ProvenanceCode.{Pascal},
                ct);

            // Cross-modal alignment edges (frame N corresponds to audio sample range [a, b]).
            // ... AddEdge(EdgeTypeCode.AlignedTo, ...)
        }
    }
}
```

The pipeline owns the recursion. The decomposer just identifies what goes in.

---

## Anti-patterns (specific to decomposers)

- **DON'T** call `Blake3.Hash` or build Merkle trees by hand in a decomposer. Use the shared `ITextCanonicalizer` / `IAudioCanonicalizer` / etc. — they call libhartonomous via the compute facade. Hashing logic lives in ONE place (the native library); reinventing it breaks convergence and Law #6.
- **DON'T** apply NFC normalization, UAX #29 segmentation, dtype decoding, or any modality-specific canonicalization in a decomposer. The shared canonicalizers do it. Same content from any decomposer must produce the same hash.
- **DON'T** open a database connection. The pipeline does.
- **DON'T** use `Channel.CreateBounded` or `Parallel.ForEachAsync`. The pipeline parallelizes its internal work.
- **DON'T** push compute into the DB. No PL/pgSQL hashing functions, no per-row triggers, no recursive procedures. Compute happens client-side via libhartonomous; the DB only does set-based INSERTs.
- **DON'T** disambiguate at ingestion. Record all candidate senses/structures; inference picks.
- **DON'T** call any LLM or model inference. Decomposers are deterministic.
- **DON'T** create your own canonicalization for a modality that already has one. If text canonicalization rules need to change, change them in `ITextCanonicalizer` / libhartonomous ONCE.

---

## Verification checklist

- [ ] `{Pascal}Source` marker exists, implements `ISourceFormat`
- [ ] Decomposer inherits `Decomposer<{Pascal}Source>`
- [ ] Decomposer body is at most: read records → submit content → submit metadata
- [ ] No `Blake3.Hash`, no Merkle building, no canonicalization in the decomposer
- [ ] No `NpgsqlConnection`, no `Channel.CreateBounded`, no `Parallel.ForEachAsync`
- [ ] DI registration in place
- [ ] CLI command and PowerShell script added
- [ ] Unit tests use a fake `IIngestionPipeline` to assert correct submissions
- [ ] Integration test ingests a fixture against real DB and asserts substrate state
- [ ] Re-running ingestion produces zero new rows (Law #6 idempotency)
- [ ] Spec doc + index updated

---

## Related recipes

- `09-add-analysis-pass.md` — for pass-based decomposers (the safetensors canonicalizer hosts these)
- `10-add-recomposer.md` — to render output from substrate state
- `17-add-test.md` — testing patterns including fake pipeline
- `18-add-cli-command.md` — CLI surface
- `19-add-phase.md` — if a new phase is needed
