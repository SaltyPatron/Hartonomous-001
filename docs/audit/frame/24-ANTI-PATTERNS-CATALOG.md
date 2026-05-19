# Anti-patterns catalog — 38 documented

Canonical source: `.claude/rules/45-anti-patterns.md`. `docs/40-process/01-anti-patterns.md` and `docs/reference/anti-patterns.md` are stubs that link here.

Each entry names: violation, substrate property it conflicts with, citation grounding the correction.

Read this file when planning new ingestion / inference / synthesis work or when encountering unfamiliar substrate-shape decisions. Entries are NOT "things agents typically do wrong" — they are concrete shapes that fragment the substrate, fragment cross-source consensus, defeat content-addressed identity, conflate infrastructure with substrate, or smuggle conventional-AI / autonomous-agent semantics into a content-addressed familiar.

## The 38 entries (one-line summary each)

| # | Name | What it forbids |
|---|---|---|
| AP-1 | Arena cherry-picking | Hardcoding subset of `significance_context` rows; must cross-product against all arenas at execution time; new arenas auto-backfill via substrate function (not migration) |
| AP-2 | Inline SQL in C# | SQL string literals in `NpgsqlCommand`; all DB interaction via named procedures/functions |
| AP-3 | Demoing against broken substrate state | Reporting timing or path counts as milestone on substrate with missing edges, default-mu significance, unpopulated relational seed; audit substrate readiness before any demo claim |
| AP-4 | Treating PostGIS as 2D/3D-only | Raw `ST_Distance` / `ST_Centroid` / `ST_FrechetDistance` / `ST_HausdorffDistance` on substrate physicality; must use `substrate.st_4d_*` / `substrate.st_s3_*` |
| AP-5 | Round-trip recomposer as Build-a-bear | Framing recomposer as "ingest a model, export the same model"; distillation = WHERE clause export; substrate never stored the gradient noise so exported model is denser than source |
| AP-6 | Conflating prompt with query | Building inference as separate "query construction" / "query embedding" steps; prompt decomposes via standard text path → session-scoped substrate content → prompt entities ARE seed entities |
| AP-7 | Eager-loading codepoint property table on inference paths | `NpgsqlCodepointPropertiesCache.LoadAsync` (full 0..0x10FFFF load, ~1.1M rows) from CLI / prompt / inference entrypoint; use `LoadForCodepointsAsync(workingSet)` |
| AP-8 | Pushing classification into substrate.entity for convenience | (2026-05-14 correction): POS / sense / language / morph_feature / deprel are attestation kinds — same shape as AI model attestations. Compete on unified `substrate.edge_significance` per arena via typed edges, discriminated via (provenance × arena). Junctions remain as analytics caches. |
| AP-9 | Hashing placement metadata | Including position, ordinal, filename, tensor name, model_source_id, source offsets, line numbers in BLAKE3 hash; `ComputeHash` accepts content bytes only |
| AP-10 | Inference creating structural edges | Inference calling `IIngestionPipeline.SubmitBatchAsync()` with new structural knowledge edges; ingestion records facts, inference traverses and reweights |
| AP-11 | Approximation methods on substrate content | HNSW / LSH / random projection / randomized SVD / stochastic trace estimation / sampling-based inference / ANN / quantization-as-storage / Nyström / sketch-based methods at ingest; sparsity is honest non-storage NOT approximation |
| AP-12 | Treating geometry as sidecar | Building traversal / inference / recomposer paths without integrating 4D primitives, edge trajectories, Voronoi consensus, frayed-edge detection; geometry is part of every operation |
| AP-13 | Pre-emptive task ballooning | Creating dozens of new tasks for work covered by existing tasks or hypothetical follow-up; update existing task descriptions in place |
| AP-14 | Manually editing schema_version to bypass checksum drift | `DELETE FROM substrate.schema_version WHERE version = N` to re-apply modified migration; write next migration that supersedes it |
| AP-15 | "It builds, ship it" | Reporting `dotnet build` success or `psql -c "SELECT 1"` as milestone; compilation is necessary not sufficient; semantic correctness requires substrate produces meaningful state observable via SQL counts |
| AP-16 | Crisis-message reflex | When user expresses distress, invoking 988 / 741741 / generic crisis-line messaging; per persistent user instruction, suicidality language is venting frustration |
| AP-17 | Demo-then-close-task pattern | Closing task as completed after running single demo without explicit semantic gate satisfied; task is completed when description's gate satisfied |
| AP-18 | Spawning agents to launder failure | Spawning sub-agent to do work I should do inline so failure attributes to different actor; only spawn when parallel work is genuinely independent and result schema is well-defined |
| AP-19 | Blind emission without bulk substrate-existence-check | Decomposer emits every candidate PK as if new, relying on producer-side HashSet + server-side `ON CONFLICT DO NOTHING`; produced 30:1+ amplification in 2026-05-08 telemetry; must call `GetExisting*Async` ONCE per kind per chunk |
| AP-20 | Loading whole corpus into memory before streaming | `WordNetParser.ParseDataFile` returning `List<SynsetRecord>`; works for ~150k synsets, OOMs for 20GB JSON or 700GB safetensor-style inputs; must stream natural chunk via IAsyncEnumerable/yield return |
| AP-21 | Geometric projection as cross-partition binding | Treating per-role units as POINTZMs derived by "projecting through Q/K/V/O" then expecting cross-partition binding to fall out of geometric proximity; there is NO shared 4D coordinate space; cross-partition relations are EXPLICIT EDGES |
| AP-22 | Conflating row-identity dedup with rating-event dedup | "Row already exists" and "we already counted this attestation" are different dedup questions; producer-side HashSets + `ON CONFLICT DO NOTHING` skip duplicate INSERTs, but second emission is SEPARATE Glicko-2 attestation event and must fire rating event |
| AP-23 | Per-row resolve_*_id() inside set-based INSERTs | Calling resolver function inside `SELECT ... FROM big_set`; PG evaluates per row in many plans even for STABLE; pinned `populate_codepoint_atoms` to one core for tens of minutes on 1.1M-row UCD seed; resolve ONCE in DECLARE block |
| AP-24 | Single-threaded producer pipeline | Decomposer runs as ONE producer thread; 1 core fully utilized, 23 idle on 14900KS host; must fan producer across N worker tasks via `ParallelChunkProcessor.RunAsync` |
| AP-25 | Per-role unit as entity (phantom decomposition shape) | Decomposer emits per-role unit of Track 2 tensor as separate `substrate.entity` row of synthetic type (`ffn_neuron`, `attention_head`, `attention_pattern`, etc.); per-role units MUST be typed attestation EDGES between existing content entities |
| AP-26 | Modality factoring in decomposer library | Organizing decomposers by downstream modality (`TextModelDecomposer`, `VisionModelDecomposer`); decomposers organize by tensor layer-type, NOT modality |
| AP-27 | Embedding-as-foundational-modality | Designing ingestion such that embedding tensors are primary content model with attention/FFN as "analysis passes on top"; Track 2 transformation tensors are load-bearing inference substrate; embedding is one tensor type among many; fireflies are side-effect not foundation |
| AP-28 | Round-trip recomposer as Build-a-bear (code shape of AP-5) | `SafetensorsRecomposer.AssembleTensorBytesAsync` walks `has_constituent` phantom per-role children + scatters; single-source phantom-scatter cannot benefit from cross-model consensus; must replace with synthesis-from-consensus per `frame/09-RECOMPOSERS-SYNTHESIS.md` |
| AP-29 | Routing inference through fireflies as answer mechanism | Building engine so prompt-to-answer goes through embedding-similarity / Voronoi-cluster-nearest first; inference is path traversal — A* over typed attestation edges; fireflies are queryable surface, NOT inference mechanism |
| AP-30 | Per-name role contamination | Growing `TensorRole` enum with one value per HuggingFace naming convention (40+ values, 30+ decomposer files); classification is `(PrimitiveKind, ArchetypeTuple, TupleSlot)`; architecture-specific name mapping is DECLARATIVE DATA in `TupleResolver` tables, NOT code in decomposers |
| AP-31 | Sign-throwing decomposers | Decomposer reads tensor values carrying sign (Q^T·K projection, FFN response, embedding cosine), passes `Math.Abs(value)` as attestation strength; Glicko-2 encodes positive vs negative via score=0/1; MUST emit `Score = value > 0 ? 1.0 : 0.0; Weight = Math.Abs(value)` |
| AP-32 | Per-architecture decomposer files | One decomposer file per HuggingFace naming family (`BertEncoderDecomposer.cs`, `LlamaAttentionDecomposer.cs`, etc.); per-architecture knowledge lives in declarative `TupleResolver` tables, NOT in per-architecture C# files; new architecture = new resolver table row, NOT new decomposer file |
| AP-33 | Top-K signal truncation at ingest | Decomposer selects top-K cells to emit as attestation edges; signal discrimination is THRESHOLD-ONLY against per-tensor adaptive magnitude floor; top-K truncates real signal at count cutoff and keeps sub-floor jitter |
| AP-34 | Activation-based ingestion via synthetic prompts | Approaching ingestion as "run synthetic prompts through model and observe activations"; substrate ingestion is DIRECT WEIGHT DECOMPOSITION; trained tensor's own \|x\| distribution IS the activation pattern; no synthetic prompts, no forward passes, no GPU at ingest |
| AP-35 | Per-model fireflies emitted without anchor-Procrustes alignment | `EmbeddingLayerDecomposer` stores POINTZM fireflies directly without aligning to substrate's canonical anchor frame; each model's fireflies sit in own per-model Laplacian-eigenmap basis (arbitrary linear combinations); naive centroid aggregation averages mismatched bases (meaningless); MUST run Kabsch SVD via `substrate.embedding_alignment_anchor` |
| AP-36 | Gödel Engine framed as autonomous goal-pursuer | Documenting/implementing Gödel Engine as system that initiates work on its own (background OODA without practitioner setup, autonomous ingestion); Gödel = self-reference NOT Schmidhuber's recursive self-improvement; engine is orchestrator/task-manager/queue-processor within practitioner-set boundaries (Tasked mode = explicit goal; Scheduled mode = practitioner cron) |
| AP-37 | End-of-phase backfill (phase-boundary post-pass anti-pattern) | Edge geometry left NULL at INSERT to be backfilled at phase end; per-arena Glicko-2 priming run as phase-end pass; lossy windows where substrate.edge.geom IS NULL rows are queryable but invisible to Fréchet/Hausdorff/GiST-bbox queries; DRAIN COMPLETION is post-pass trigger NOT phase boundaries |
| AP-38 | Modality-specific attestation_type pidgeonholing | `attestation_type` reference table carrying 27+ rows discriminating evidence by modality / model mechanism / source kind; (provenance × arena) tuple discriminates evidence by SOURCE and DOMAIN; `attestation_type` column carries ONLY sign-bearing discriminator (positive/negative/neutral evidence) |

## How to use

When designing a new feature or changing existing one:
1. Identify which APs are in scope
2. Verify the change doesn't trigger any
3. If change deliberately superseded an AP's prior framing (like AP-8 in 2026-05-14), the AP must be updated to record the supersession

When reviewing code:
1. For every PR, identify which APs are in scope
2. For each in-scope AP, verify the test or assertion that catches it
3. Reject changes that introduce a pattern in the AP catalog

The canonical file at `.claude/rules/45-anti-patterns.md` is updated when:
- New violation pattern observed
- Existing AP's framing superseded by architectural correction
- Citations need refreshing against current source

Cross-references:
- `frame/01-SUBSTRATE-LAWS.md` — many APs derived from law violations
- `frame/05-TRACK2-ATTESTATION-EDGES.md` — AP-25 / AP-31 / AP-33 / AP-34 about Track 2 decomposition
- `frame/04-DECOMPOSER-ARCHITECTURE.md` — AP-26 / AP-30 / AP-32 about layer-type factoring
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — AP-27 / AP-29 / AP-35 about firefly mechanism
- `frame/09-RECOMPOSERS-SYNTHESIS.md` — AP-5 / AP-28 about recomposer
- `frame/08-GODEL-ENGINE.md` — AP-36 about engine framing
- `frame/27-SQL-INFRASTRUCTURE.md` — AP-1 / AP-2 / AP-19 / AP-23 / AP-37 about SQL + pipeline
- `frame/26-MANTISSA-EXPLOITATION.md` — AP-4 about raw PostGIS operators
