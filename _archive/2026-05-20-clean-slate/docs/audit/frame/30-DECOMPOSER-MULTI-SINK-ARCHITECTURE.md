# Decomposer multi-sink architecture (pre-gen perf cache + substrate seed share parse logic)

User correction 2026-05-19. Stale memory (`memory/feedback-pre-gen-vs-substrate-ingest-strict-split.md`, `memory/project-pre-gen-not-substrate-ingestion.md`) and related rule text documented "two pipelines from same source files, never feed each other; write a C# parser, duplicate the parsing work." **This is the wrong architecture.** Documenting the corrected one here so future agents don't perpetuate the stale framing.

## What the stale architecture said

- **Pre-gen** (build-time): Python `scripts/build/generate_unicode_tables.py` + C `ext/libhartonomous/codegen/gen_ucd_*.c` reads source content (`ucd.all.flat.xml`, `allkeys.txt`, etc.), emits flat C arrays / mmap blobs compiled into `hartonomous.so` for microsecond client-side lookups (`PhysicalityEmitter.CodepointS3Position`, `hartonomous_ucd_cp_centroid`).
- **Substrate ingestion** (runtime): C# decomposers under `src/Hartonomous.Decomposers/` parse the SAME source files directly, emit via `IIngestionPipeline.EmitAsync` into substrate.* tables with Glicko-2 events, per-arena priming, sign-aware attestations.
- **Two pipelines from the same source files, disjoint consumers, never feed each other.**
- The `populate_*_from_ext` SQL functions tried to bolt pre-gen output to substrate ingestion — explicitly banned because they bypass substrate ingestion invariants (drain-completion post-pass, bulk substrate-existence-check, AP-1 arena priming, sign-aware Glicko, multi-version attestations).
- Solution per stale memory: "write a C# parser for the source file(s), emit through `IRecordSink.EmitAsync` like every other decomposer" — i.e., DUPLICATE the parsing work.

This is the wrong architecture. Documenting it here was about to perpetuate the duplication.

## What the correct architecture is

**ONE decomposer reads source content ONCE. ONE decomposition pass. Output forks to MULTIPLE SINKS:**

```
                    ┌──────────────────────────────────────────┐
                    │  Decomposer (parses source content once) │
                    │  e.g. UCD XML walker, UCA allkeys parser │
                    └────────────────────┬─────────────────────┘
                                         │
                            (typed record stream)
                                         │
                          ┌──────────────┴──────────────┐
                          │                             │
                          ▼                             ▼
                ┌─────────────────────┐     ┌──────────────────────┐
                │ Pre-gen sink         │     │ Substrate-seed sink  │
                │ (build-time only)    │     │ (IIngestionPipeline) │
                │                      │     │                      │
                │ Materializes record  │     │ Emits records via    │
                │ stream into flat C   │     │ IRecordSink.EmitAsync│
                │ arrays / mmap blob   │     │ → substrate.entity / │
                │ compiled into        │     │ edge / physicality / │
                │ hartonomous.so       │     │ classification with  │
                │                      │     │ Glicko-2 events,     │
                │ Used by hot-path     │     │ per-arena priming,   │
                │ client lookups       │     │ sign-aware           │
                │ (microsecond O(1))   │     │ attestations         │
                └─────────────────────┘     └──────────────────────┘
```

Same parser. Same record stream. Two independent sinks consume.

## Why this is the right shape

1. **Parsing the source is the expensive part.** ucd.all.flat.xml is ~150MB; walking it to extract per-codepoint properties is the dominant cost. Doing it twice (once for pre-gen Python parser, once for C# substrate decomposer) is wasted work AND introduces correctness drift risk (two parsers might extract slightly different values).

2. **Sinks are cheap to add.** Once you have the typed record stream (`{codepoint: U+0041, general_category: Lu, script: Latin, block: Basic_Latin, ...}`), emitting to either the pre-gen materializer or `IRecordSink.EmitAsync` is constant per record. No reason to constrain the architecture to a single consumer.

3. **Pre-gen IS a build-time materialization of substrate seed.** Same boundary discipline as analytics caches per `frame/10-CRYSTAL-BALL-ANALYTICS.md`. Substrate state is the truth; pre-gen is a cache rebuildable from substrate state (or, equivalently, rebuildable from the same parse run that produces the substrate seed). The pre-gen artifact is a hot-path index over the substrate-seeded data, not a separate truth.

4. **Pre-gen needs the same correctness gates as substrate seed.** Anything that would invalidate the substrate seed (parser bug, source-file change, schema version bump) ALSO invalidates the pre-gen artifact. Sharing the parse logic ensures both rebuild together when either's invariant changes.

5. **Build modes follow naturally.**
   - `--sink=pregen-only` — build-time codegen run that produces extension blob without populating substrate (used for extension distribution where downstream consumers do their own substrate ingestion)
   - `--sink=substrate-only` — runtime ingestion at the practitioner's substrate without re-emitting pre-gen (because pre-gen is already compiled into hartonomous.so from the extension build)
   - `--sink=both` — bootstrap run that ingests substrate seed AND emits codegen artifacts in one pass (typical full-build pipeline)

## Decomposer contract this enables

Per the trinity-axis emission framing (`frame/25-TRINITY-AXIS-EMISSION.md`), every decomposer produces records that factor into:
- Axis 2 emissions: app data refs / user data emissions / substrate data emissions
- Axis 1 source: where the data came from

For UCD decomposition specifically:
- Axis 1 source: seed corpus (UCD/UCA shipped with substrate)
- Axis 2 emissions:
  - App data: codepoint entities + their canonical UCD property assignments
  - User data: none (UCD is canonical structural reference, not modality-bound content)
  - Substrate data: per-Unicode-version attestation events on each codepoint property (30 UCD versions → up to 30 attestation events per per-version-changed property under `unicode_version_consensus` arena)

The SAME record stream goes to:
- Pre-gen sink — materializes per-codepoint property lookups into C arrays for hot-path use
- Substrate-seed sink — emits codepoint atoms + property junction rows + per-version attestation Glicko events into substrate.*

Pre-gen sink takes the LATEST version's collapsed property values (one row per codepoint). Substrate-seed sink keeps the FULL per-version attestation history (one Glicko event per version per property change).

Both consume the same typed record stream. The pre-gen sink is a stricter / collapsed view; the substrate-seed sink is the full evidence history.

## How the C# decomposer plumbs this

```csharp
// Single parser, multiple sinks
public class UcdGroupedXmlDecomposer
{
    private readonly IPreGenSink _preGen;          // null when not building extension
    private readonly IRecordSink _substrateSink;   // null when pre-gen-only build

    public async ValueTask DecomposeAsync(string ucdXmlPath, CancellationToken ct)
    {
        await foreach (var record in ParseUcdXml(ucdXmlPath, ct))
        {
            // Each sink decides what it cares about; multi-sink fan-out is constant per record
            _preGen?.Emit(record);
            await (_substrateSink?.EmitAsync(record, ct) ?? ValueTask.CompletedTask);
        }
    }

    private async IAsyncEnumerable<UcdRecord> ParseUcdXml(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        // Streaming XML pull-parser; one pass over the file
        // Yield UcdRecord for each <char> element with all relevant properties extracted
        ...
    }
}
```

Either sink can be null (single-sink build). Both non-null = full-build pipeline. Same parse-once, fan-out-cheaply pattern. No duplication of parsing logic.

## What the `populate_*_from_ext` anti-pattern was really about

The stale memory says `populate_*_from_ext` is "the corner-cut that bolted the perf cache onto substrate seeding and must be retired." That's correct AS FAR AS IT GOES — `populate_*_from_ext` IS the wrong shape (reading the compiled extension blob's SRFs back into substrate.* via PL/pgSQL bypasses every ingestion invariant). But the CORRECTION the stale memory proposes is also wrong (duplicating the parse logic).

The correct correction:
- DELETE `populate_*_from_ext` SQL functions — agreed
- DELETE Python `generate_unicode_tables.py` as a SEPARATE parser — disagreed with stale memory; merge its parse logic into the same C# decomposer that emits substrate seed
- KEEP the pre-gen sink — but route it through the same record stream the substrate-seed sink consumes
- KEEP `hartonomous_ucd_cp_centroid` / `hartonomous_ucd_*` blob exports — but they're built from the same record stream, not from a separate Python parse

This means there's ONE canonical UCD-XML walker in the codebase, and at build time it emits to whichever sink(s) are configured. Pre-gen is a build-mode of the same decomposer that produces substrate seed.

## Where the stale architecture exists in current code

(Verification needed — flagged in PENDING.md for Phase C source reading)

- `scripts/build/generate_unicode_tables.py` — Python pre-gen parser. Probably to be retired or merged.
- `ext/libhartonomous/codegen/gen_ucd_flat.c` — C codegen walker for UCD XML. Either becomes the canonical parser (with a sink interface) or merges into a C# canonical parser.
- `ext/libhartonomous/codegen/CMakeLists.txt` — build-time invocation of codegen.
- `ext/hartonomous_pg/src/generated/pg_ucd_segmentation.{h,c}` — generated headers compiled into PG extension.
- `ext/hartonomous_ucd_embedded/` — UCD blob embedding library.
- `populate_*_from_ext` SQL functions — slated for removal per stale memory; corrected architecture still removes them but for a different reason (they bolt the perf cache onto substrate ingestion bypassing invariants; the fix is the multi-sink decomposer that emits to substrate ingestion via the standard pipeline, not via blob read-back).

## Implication

Every "build-time perf cache" the substrate produces (UCD blob, UCA collation table, ISO 639 language lookup, CLDR locale data, anything else compiled into hartonomous.so for client-side hot-path lookup) should follow this pattern: **single decomposer, multi-sink emission**. The pre-gen output is a build-time materialization of the substrate seed for hot-path indexed access — not a separate parse pipeline.

This is the same pattern as the substrate's analytics-cache discipline (`frame/10-CRYSTAL-BALL-ANALYTICS.md`): substrate state is the truth; derived hot-path indexes are caches rebuildable from substrate state. Build-time pre-gen is just the special case where the rebuild happens at build time (because the source data is canonical and unchanging within a substrate version).

## Cross-references

- `frame/22-NATIVE-COMPUTE-FACADE.md` — `gen_ucd_flat.c` + `hartonomous_ucd_cp_centroid` blob (needs correction: pre-gen is a sink off the same parse run that emits substrate seed, not a separate parser)
- `frame/10-CRYSTAL-BALL-ANALYTICS.md` — substrate state vs analytics cache boundary discipline (pre-gen follows same discipline at build time)
- `frame/25-TRINITY-AXIS-EMISSION.md` — per-decomposer Axis 2 emission factoring; multi-sink decomposer factors emissions the same way
- `frame/24-ANTI-PATTERNS-CATALOG.md` — AP-37 (no phase-boundary backfill) — multi-sink decomposition runs in one pass at build time, no phase boundaries
- `frame/31-UCD-CANONICAL-INVENTORY.md` — the canonical UCD source files this decomposer reads
- `frame/27-SQL-INFRASTRUCTURE.md` — `populate_*_from_ext` deletion confirmed; the corrected substrate-seed sink uses the standard `IIngestionPipeline` path

## What this audit notes for the canonical surface

The current `.claude/rules/00-hartonomous-core.md` rule + `.claude/rules/45-anti-patterns.md` AP-37-adjacent text + memory files DOCUMENT THE STALE ARCHITECTURE. They need correction when Phase D canonical surface is designed. Specifically:

- `.claude/rules/00-hartonomous-core.md` "Pre-gen ≠ substrate ingestion" section needs rewriting — it currently says pre-gen and substrate ingestion are "two layers; don't conflate." Correct framing: "two SINKS of the same decomposition pipeline; don't duplicate the parser."
- Memory `feedback-pre-gen-vs-substrate-ingest-strict-split.md` needs supersession — the "write a C# parser, duplicate the parsing work" prescription is wrong.
- Memory `project-pre-gen-not-substrate-ingestion.md` keeps the basic two-layer distinction (which is correct in spirit — they ARE distinct sinks) but needs the multi-sink shared-parser correction.
- AP-37 catalog stays correct (drain-completion as post-pass trigger); the related anti-pattern about `populate_*_from_ext` stays correct (don't bolt perf cache onto substrate ingest); the proposed FIX in those docs is what's wrong.
