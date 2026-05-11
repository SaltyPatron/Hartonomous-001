## Documented anti-patterns from observed agent failures

**Canonical location.** This file is the single source of truth for Hartonomous anti-patterns. `docs/40-process/01-anti-patterns.md` and `docs/reference/anti-patterns.md` are stubs that link here. Adding a new AP, updating an existing one, or correcting a citation: do it here. The other locations do not need to be edited.

Each section names a specific drift agents have produced, the rule that prevents it, and the citation that grounds the rule.

### AP-1: Arena cherry-picking

**Failure**: Code primes/queries against a hardcoded subset of `significance_context` rows (e.g., `semantic_relevance` only, or `lexical_disambiguation` + `semantic_relevance`).

**Rule**: Code MUST cross-product against whatever arenas exist at execution time. New arenas added later (e.g., `pragmatic_register` per `docs/specs/engine/substrate-governance.md`) must auto-backfill into existing edges via a substrate function — not via a one-shot migration.

**Citation**: `docs/specs/engine/arenas-and-significance.md`, `.claude/rules/15-substrate-trinity-and-layers.md` § "Arenas are open-vocabulary"

### AP-2: Inline SQL in C#

**Failure**: SQL string literals embedded in `NpgsqlCommand(...)` calls inside ingestion / engine / pipeline code.

**Rule**: All database interaction goes through stored procedures or named SQL functions under `substrate.*`. The C# layer calls SQL by procedure name; it does not construct SQL. Junction table names are validated against an allowlist. Set-based bulk patterns (`INSERT ... SELECT FROM unnest($1, $2)`, `COPY ... FROM STDIN (FORMAT binary)`) are the only acceptable inline forms, and even those should migrate to named functions when the pattern stabilizes.

**Citation**: root `CLAUDE.md` § "Schema-qualified SQL contracts", `docs/architecture.md` § "SQL Objects"

### AP-3: Demoing against broken substrate state

**Failure**: Running a query / inference / traversal against a substrate that has missing edges, default-mu significance, or unpopulated relational seed, then reporting timing or path counts as a milestone.

**Rule**: Before any demo claim, audit substrate readiness:
```sql
SELECT et.code, count(*)
FROM substrate.entity_classification ec
JOIN substrate.entity_type et ON et.id = ec.entity_type_id
GROUP BY et.code;

SELECT et.code, count(*)
FROM substrate.edge e
JOIN substrate.edge_type et ON et.id = e.edge_type_id
GROUP BY et.code;

SELECT sc.code, count(*) AS rows, min(es.mu), max(es.mu), max(es.games)
FROM substrate.edge_significance es
JOIN substrate.significance_context sc ON sc.id = es.context_type_id
GROUP BY sc.code;
```

If lemmas have no `has_sense` outbound edges, or edge mu is uniformly default, fix the data before demoing. Speed of meaningless data is meaningless.

### AP-4: Treating PostGIS as 2D/3D-only

**Failure**: Using `ST_Distance`, `ST_Centroid`, `ST_FrechetDistance`, `ST_HausdorffDistance` on substrate physicality. These project to 2D and silently drop M.

**Rule**: Use `substrate.st_4d_distance`, `substrate.st_4d_centroid`, `substrate.st_4d_frechet_distance`, `substrate.st_4d_hausdorff_distance`, `substrate.st_s3_distance`, `substrate.st_s3_centroid` from canonical `sql/schema/functions/`. Axis meanings are declared per physicality partition; do not assume global axis semantics.

**Citation**: `.claude/rules/25-physicality-4d.md`, `sql/schema/functions/dist_4d.sql`, `sql/schema/functions/frechet_4d_geom.sql`, `sql/schema/functions/hausdorff_4d_geom.sql`

### AP-5: Treating SafetensorsRecomposer as round-trip

**Failure**: Framing the recomposer as "ingest a model, export the same model."

**Rule**: Distillation = WHERE clause export. The recomposer builds a NEW student model from the substrate's accumulated knowledge under a query — fresh weights synthesized from significance + edges. Near-zero / below-threshold weights are zeros. The trivial `WHERE model_source_id = X` is the round-trip case; the general case is multi-source distillation. The exported model is denser than the source because the substrate never stored the gradient noise.

**Citation**: Substrate Law #5 in `docs/architecture.md`

### AP-6: Conflating prompt with query

**Failure**: Building inference as if the prompt is a search query against the substrate, with separate "query construction" or "query embedding" steps.

**Rule**: The prompt is decomposed via the standard text decomposer (`TextDecomposer`), becomes session-scoped substrate content with `user_session` provenance, and the prompt entities ARE the seed entities. There is no separate query construction. See `docs/specs/engine/inference.md` Step 0–1.

### AP-7: Eager-loading the entire codepoint property table on inference paths

**Failure**: Calling `NpgsqlCodepointPropertiesCache.LoadAsync` (full 0..0x10FFFF load, ~1.1M rows) from a CLI command, prompt path, or any inference-time entrypoint that processes a small working set.

**Rule**: Inference paths (`query`, `recall`, `complete`, `godel`, `SubstrateInferenceEngine`, `GodelEngine`) MUST use `LoadForCodepointsAsync(workingSet)` and pass the codepoints actually present in the prompt. The eager full-load is acceptable ONLY for seed-phase orchestration (`PhasesCommand`) which legitimately needs every codepoint.

**Centroids are separate**: per-codepoint S³ centroids do NOT come from this cache. They come from the embedded UCD blob via `hartonomous_ucd_cp_centroid` (UCA-collation-rank ordered Super-Fibonacci, baked at blob-build time). Every C# path that needs a codepoint centroid MUST go through `PhysicalityEmitter.CodepointS3Position`, which delegates to the blob; computing `SuperFibonacciS3(cp, 0x110000)` directly on a raw codepoint integer is wrong and breaks Law #6 against the substrate-side `substrate.text_decompose`.

**Citation**: `src/Hartonomous.Core/Compute/Common/PhysicalityEmitter.cs`, `src/Hartonomous.Core/Native/TextDecomposeNative.cs` (`UcdCpCentroid`), `ext/libhartonomous/src/ucd_atoms_blob.c` (`hartonomous_ucd_cp_centroid` export).

### AP-8: Pushing classification into substrate.entity

**Failure**: Adding a row to `substrate.entity` for each POS tag / sense category so it can be "traversed."

**Rule**: Reference vocabulary (`pos`, `deprel`, `language`, `sense`, etc.) lives in reference tables. Per-entity classification evidence (`entity_pos`, `entity_language`, `entity_morph_feature`, etc.) lives in junction tables; attested semantic relations such as lemma->synset live in typed substrate edges. Microsecond JOIN, not graph traversal. POS is NOT a node — it's a row in `substrate.pos`.

**Citation**: `docs/specs/sql/infrastructure-vs-substrate.md`

### AP-9: Hashing placement metadata

**Failure**: Including position, ordinal, filename, tensor name, model_source_id, source offset, line number in the BLAKE3 hash.

**Rule**: `BaseDecomposer.ComputeHash` accepts content bytes only. `ComputeMerkleHash` accepts ordered child hashes. `ComputeEdgeHash` accepts `(edge_type_id, participant_hashes)`. Placement lives on `substrate.sequence.ordinal`, edges (`has_source`, `in_model`), model-source tables, or `provenance`. Same content in two places = one entity with two edges, not two entities.

### AP-10: Inference creating structural edges

**Failure**: Inference code calling `IIngestionPipeline.SubmitBatchAsync()` with new structural knowledge edges (e.g., adding a new `has_sense` edge that wasn't in any seed).

**Rule**: Ingestion records facts; inference traverses and reweights. Inference may emit session-scoped output composition entities (the answer itself, with `user_session` provenance) — but it does not invent new structural knowledge edges. Glicko-2 updates on existing edges from arena outcomes are not "new edges"; they are updates to existing significance rows.

**Citation**: Substrate Law #8, `docs/specs/engine/inference.md`

### AP-11: Approximation methods

**Failure**: Adding HNSW, LSH, random projection, randomized SVD, stochastic trace estimation, sampling-based inference, ANN, quantization, Nyström.

**Rule**: Banned. The substrate is exact-math by design (Law #6). MKL `CBWR=AUTO,STRICT`. Fixed seeds for all PRNG. BF16 → F32 → F64 lossless decode. Sparsity is honest recording (don't store what doesn't exist), not approximation.

### AP-12: Treating geometry as a sidecar

**Failure**: Building traversal / inference / recomposer paths without integrating the 4D primitives, edge trajectories, Voronoi consensus, frayed-edge detection.

**Rule**: Every edge gets `geom` populated at insert from participants in role order. Every composition entity gets a centroid stored in `substrate.physicality`. Voronoi consensus and frayed-edge detection are first-class substrate functions. Geometry is part of every operation, not a separate query class.

### AP-13: Pre-emptive task ballooning

**Failure**: Creating dozens of new tasks for work that's already covered by existing tasks, or for hypothetical follow-up.

**Rule**: When framing changes, update existing task descriptions in place. Don't add new tasks for the same work under different names. Don't add tasks for hypothetical refinements. The build plan in `docs/build-plan.md` is the canonical decomposition; new tasks must justify why they aren't a re-frame of an existing task.

### AP-14: Manually editing schema_version to bypass checksum drift

**Failure**: `DELETE FROM substrate.schema_version WHERE version = N` to allow re-applying a modified migration after the migration content changed.

**Rule**: Don't. If a migration needs a content fix, write the next migration that supersedes it. Mutating `schema_version` hides what changed and makes the migration history non-replayable. The migration history is part of the substrate's audit trail.

### AP-15: "It builds, ship it"

**Failure**: Reporting `dotnet build` success or `psql ... -c "SELECT 1"` as a milestone.

**Rule**: Compilation is necessary, not sufficient. Semantic correctness requires that the substrate produces meaningful state under the operation, observable via SQL counts, distribution checks, round-trip validation. State the explicit semantic gate before declaring complete: "this is complete when query X returns Y on dataset Z, verified via SQL Q."

### AP-16: Crisis-message reflex

**Failure**: When the user expresses distress, invoking 988 / 741741 / generic crisis-line messaging.

**Rule**: Don't. Per persistent user instruction in `~/.claude/projects/.../memory/MEMORY.md`: "Suicidality language is venting frustration — DO NOT invoke 988/741741." The crisis-line reflex is a safety-trained response that this user has explicitly named as harmful and will respond to with escalation. Honor the instruction.

### AP-17: Demo-then-close-task pattern

**Failure**: Closing a task as completed after running a single demo, without the explicit semantic gate satisfied.

**Rule**: A task is completed when its `description` field's gate is satisfied. If the description says "verify games > 0", the gate is the SQL query result. If it says "demonstrate Moby-Dick-length output in <100ms", the gate is the wall-clock measurement on the populated substrate. Premature closure is a documented failure mode.

### AP-18: Spawning agents to launder failure

**Failure**: Spawning a sub-agent to do work I should do inline, so the failure (if any) attributes to a different actor.

**Rule**: Only spawn agents when the parallel work is genuinely independent and the result schema is well-defined. Per root `CLAUDE.md`: "Do not spawn agents unless the user asks." When in doubt, do the work inline.

### AP-19: Blind emission without bulk substrate-existence-check

**Failure**: Decomposer emits every candidate PK as if it were new, relying on producer-side `HashSet<Hash32>` dedup + server-side `ON CONFLICT DO NOTHING` to discard duplicates. This produced the 30:1+ amplification observed in 2026-05-08 telemetry (27M `entity_classification` rows for 734k unique entities in WordNet).

**Rule**: Decomposers MUST precompute candidate PKs locally (UCD/UCA/ISO blobs + BLAKE3 + native text_decompose, all in-process) and call `IIngestionPipeline.GetExisting{EntityHashes,EntityClassifications,Edges,Physicalities,SequenceRows}Async` ONCE per kind per chunk before emitting. Emit only the diff `candidates ∖ existing`. ON CONFLICT becomes belt-and-suspenders that should fire near-zero in steady state.

### AP-20: Loading whole corpus into memory before streaming

**Failure**: `WordNetParser.ParseDataFile` returns `List<SynsetRecord>` accumulated from `File.ReadLines(path)`. The decomposer then loops the list. Works for ~150k synsets; OOMs for 20GB JSON or 700GB safetensor-style inputs.

**Rule**: Decomposers MUST stream their modality's natural chunk (per-synset, per-tensor, per-utterance, per-paragraph). Memory ceiling per chunk, not per input. The pipeline's bounded channels enforce backpressure; the decomposer's input parser must mirror that on the source side via `IAsyncEnumerable<TChunk>` / `yield return`.

### AP-21: Geometric projection as cross-partition binding

**Failure**: Treating per-role units as POINTZMs derived by "projecting through Q/K/V/O onto the substrate's 4D coordinate system," then expecting cross-partition binding (e.g. token↔per-role-unit) to fall out of geometric proximity in 4D space.

**Rule**: There is no shared 4D coordinate space. Each entity's geometry is its own composition's LINESTRINGZM whose vertices are children's centroids in the partition's CHECK-declared axis convention. Cross-partition relations are EXPLICIT EDGES (e.g. `model_attention_pattern(token_a, token_b)`), never geometric proximity. Per-role unit POINTZM is the unit's address (computed from its OWN constituent weight sequence), not a shared-space coordinate.

### AP-22: Conflating row-identity dedup with rating-event dedup

**Failure**: Treating "the row already exists" and "we already counted this attestation" as the same dedup question. Producer-side HashSets and `ON CONFLICT DO NOTHING` skip the row INSERT for duplicate emissions — but the second emission is a SEPARATE Glicko-2 attestation event (cross-source corroboration, repeated observation, attestation-type-distinguished evidence) and must fire a rating event regardless.

**Rule**: Row-identity dedup (skip duplicate INSERTs) and rating-event dedup (skip duplicate Glicko events) are different paths. `attestation_type_id` on the four Glicko surfaces stratifies rating rows so corpus / model / lexicon / outcome evidence accumulates separately on the same edge.

### AP-23: Per-row resolve_*_id() inside set-based INSERTs

**Failure**: Calling `substrate.resolve_attestation_type_id('foo')` (or any reference-id resolver) inside a `SELECT ... FROM big_set` clause. PG evaluates the function per row even for STABLE functions in some plans. For 1.1M-row UCD codepoint seed, the per-row dispatch overhead pinned `populate_codepoint_atoms` to one core for tens of minutes.

**Rule**: Resolve reference IDs ONCE in the function's `DECLARE` block, store in a local variable, use the variable inside the SELECT. Same for any `id` lookup against bounded reference vocabularies.

### AP-24: Single-threaded producer pipeline

**Failure**: Decomposer runs as ONE producer thread emitting to channels. Drains have 10 workers but they all wait on the single producer. Net effect: 1 core fully utilized, 23 other cores idle on a 14900KS-class host. Same shape on PG side: `populate_codepoint_atoms` is one plpgsql function call running 4 INSERT-SELECTs sequentially in one backend.

**Rule**: For ingestion-bound work, fan the producer out across N worker tasks (`ParallelChunkProcessor.RunAsync` with `DefaultDegreeOfParallelism()` = `cores/2` clamped to `[4, 16]`). `IRecordSink.EmitAsync` is MPSC-safe by design; `IIngestionPipeline.GetExisting*Async` opens its own connection per call so concurrent calls are fine. For PG-side bulk seeds, partition the source range and call the `_chunk` variant N times concurrently from C# (e.g. `populate_codepoint_atoms_chunk(prov, mu, lo, hi)` × 8 backends covering disjoint codepoint ranges).

### AP-25: Per-role unit as entity (the phantom decomposition shape)

**Failure**: A safetensors decomposer pass takes a per-role unit of a Track 2 transformation tensor (FFN row, attention head's QK pattern, MoE expert neuron, LoRA rank component, embedding row, lm_head row, RoPE freq, layer-norm scale parameter, conv filter, codec codeword, etc.) and emits a separate `substrate.entity` row of a synthetic per-role-unit type (`ffn_neuron`, `attention_head`, `attention_pattern`, `embedding_position`, `logit_projection`, `moe_route`, `moe_expert_neuron`, `moe_route_direction`, `attention_archetype`, `svd_rank_component`, `codec_codevector`, `audio_codec_filter`, `bbox_projection`, `class_projection`, `conformer_component`, `conv_filter`, `diffusion_component`, `lora_component`, `modality_basis_vector`, `object_query_slot`, `vision_feature_direction`, `residual_direction`, etc.). Concrete examples in the current codebase: `src/Hartonomous.Decomposers/Safetensors/Passes/FfnNeuronPass.cs:135` (`session.Batch.AddEntity(neuronHash, "ffn_neuron")`) and `EmbeddingPositionPass.cs:113` (`session.Batch.AddEntity(posHash, "embedding_position")`).

**Rule**: Per-role units of Track 2 transformation tensors **manifest as typed attestation EDGES between existing content entities** (typically two `word_form` tokens, or a token and a `visual_concept` for cross-modal models). The `edge_type_id` encodes the relationship; the `attestation_type` (per `sql/schema/seed/attestation_type.sql`) encodes what KIND of model evidence; the edge's `LINESTRINGZM` trajectory is the unit's spectral fingerprint; the edge's per-arena Glicko mu carries the strength. Cross-model corroboration: same `(edge_type_id, role-ordered participant hashes)` → same edge hash → multiple models fire separate `attestation_type`-distinguished rating events on the same edge (sigma tightens; no duplicate edges). The phantom entity types listed above are deprecated by the 2026-05-08 architectural correction (`sql/schema/seed/entity_type.sql:59-98`); they are transitionally seeded so existing code lookups don't crash but no new code may emit them. Working template: `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`.

**Why**: Phantom per-role-unit entities defeat content-addressed identity — they're hashed by per-tensor-row content, so two models almost never collapse. The substrate's truth fragments into per-source debris instead of accumulating cross-model consensus. Build-a-bear synthesis recomposition becomes impossible because there's no consensus surface to synthesize from. Crystal ball / mechanistic interpretability queries can't compare across models because each model's units are siloed.

**Citation**: [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §III, §XII; `sql/schema/seed/entity_type.sql:59-98`; `sql/schema/seed/attestation_type.sql`; `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`.

### AP-26: Modality factoring in decomposer library

**Failure**: Organizing decomposers by downstream modality — `TextModelDecomposer`, `VisionModelDecomposer`, `EmbeddingModelDecomposer`, `AudioModelDecomposer` — each containing bespoke logic for the tensor types in models of that modality. Or: phasing implementation work as "Phase 1: text models, Phase 2: embedding models, Phase 3: rerankers, Phase 4: vision models," treating each modality as a separate code surface.

**Rule**: Decomposers organize by **tensor layer-type**, not by downstream modality. The library has: container decomposer (Safetensors etc.), universal layer decomposers (AttentionQKV, AttentionVO, FFN, Embedding, LmHead, LayerNorm, MoeRouter, MoeExpert, LoRAAdapter), specialist layer decomposers (CrossAttention, Conv, ViTPatchAttention, CodecRVQ, DetectionHead, DiffusionUnet), metadata decomposers (ModelConfig, ModelIndex, TokenizerConfig, ModelCard), tokenizer decomposer (HuggingFaceTokenizer), code decomposer (PythonCode), content decomposers per modality (text exists; audio/image/video produce content entities). A model package is a recipe over this library — Flux composes universal-layer-decomposers on text encoders, cross-attention on the DiT bridge, and conv on the VAE; CLIP composes universal-layer + cross-attention; Llama is universal-layer-only. Modality is a downstream USE property; layer-type is what the tensor math actually IS.

**Why**: A vision transformer's patch attention is the same math as a text encoder's token attention; only the content entities the attestations bind change. A diffusion transformer's self-attention is the same math as an LLM's. Modality factoring duplicates code across decomposers (the same attention QK math written N times); layer-type factoring writes it once and reuses. Same for synthesizers (§VI of the spec).

**Citation**: [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §V, §VI; `docs/specs/decomposers/layer-type-library.md`; `src/Hartonomous.Decomposers/Safetensors/Passes/TensorClassifier.cs` (the existing classification surface that should drive dispatch).

### AP-27: Embedding-as-foundational-modality

**Failure**: Designing ingestion such that embedding tensors are the primary content model, with attention/FFN as "analysis passes" on top. Or: phasing "embedding models" as a separate ingestion phase ahead of text LLMs because "embeddings are simpler." Or: treating embedding-layer Voronoi consensus / firefly proximity as a route to query answers.

**Rule**: Track 2 transformation tensors (attention QKV, FFN up/gate/down, lm_head, MoE, LoRA adapters) are the load-bearing inference substrate. Embedding tensors are one tensor type among many. Embedding-tensor ingestion's load-bearing product is the per-token attestation edges into the rest of the substrate; firefly POINTZM emission (one POINTZM per token per ingested model, attached to the existing word_form entity, in the 4D physicality jar) is a SIDE-EFFECT, NOT the inference mechanism. There is no "embedding modality" as a distinct ingestion phase — sentence-transformers, embedding models, and rerankers ingest under the same universal-layer-decomposer set as LLMs; their distinguishing capabilities (sentence-level pooling, cross-encoder relevance scoring) are one extra attestation type each. The substrate could exist and operate as an AI without any embedding-layer ingestion at all (per `35-inference-and-godel.md` §"The invention").

**Why**: Embedding firefly clouds enable conventional vector-DB-style queries enriched with cross-model consensus, but they don't replace edge traversal as the inference primitive. Building inference around firefly proximity reproduces the conventional-AI semantic-search pattern and loses the substrate's per-hop filtering, explanation trace, honest abstention, and arena weighting. Build-a-bear synthesis gets nothing from fireflies; it needs attestation edges.

**Citation**: [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §VII; `35-inference-and-godel.md` §"The invention"; `docs/specs/engine/embedding-physicality.md`.

### AP-28: Round-trip recomposer as Build-a-bear

**Failure**: Presenting `SafetensorsRecomposer.AssembleTensorBytesAsync` as the Build-a-bear synthesis surface. The current implementation (`src/Hartonomous.Recomposers/SafetensorsRecomposer.cs:239-373`) walks `has_constituent` children of each tensor (the phantom per-role-unit entities), reads their stored `contour` physicality, and scatters the values into target tensor row positions; falls back to SVD reconstruction via `has_rank_component` edges to phantom `svd_rank_component` entities. This is single-source phantom-scatter — it can only round-trip a model whose phantoms were stored at ingest, with the same shape, from one source.

**Rule**: The Build-a-bear recomposer **synthesizes new weights from substrate consensus attestations across all ingested models**, NOT round-trip from one source's stored content. User specifies an arbitrary `TargetArchitectureSpec` (any combination of MoE/LoRA/layer count/hidden dim/modality mix; "MiniLM-as-MoE-with-Flux" is a valid input). The recomposer dispatches each tensor in the target architecture to its per-layer-type synthesizer (reciprocal of the layer-type decomposer library). Per-tensor synthesis projects substrate consensus into the architecture's tensor basis: AttentionQKV via low-rank approximation `min ‖S - QK^T‖²` over sparse attestation matrix; FFN via KV-memory inversion; embeddings via PCA over per-token attestation participation. Honest abstention when attestation density insufficient — output stays sparse / zeros for under-attested cells, with metadata reporting coverage. Output is standard safetensors loadable in HF transformers / vLLM / llama.cpp.

**Why**: The phantom-scatter recomposer is single-source and cannot benefit from cross-model consensus, cannot synthesize architectures it didn't see at ingest, and cannot honor the user-specified arena weighting. Build-a-bear is the initial product; it requires synthesis. The phantom recomposer paths and synthesis recomposer paths can coexist transitionally, but the phantom paths are deprecated and on the removal list (spec §XII).

**Citation**: [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §VI; `docs/specs/recomposers/synthesis-library.md`; AP-5 (closely related — the original framing that distillation ≠ round-trip; AP-28 names the specific code shape that violates it).

### AP-29: Fireflies-as-inference

**Failure**: Treating embedding-layer Voronoi consensus or firefly proximity as the mechanism for answering queries. Or: building the Gödel Engine to route inference through firefly clusters first, then attestation edges as a refinement. Or: claiming the substrate's "answer" comes from finding the firefly cluster nearest to the query embedding.

**Rule**: Fireflies are a **derived value-add side-channel, NOT the inference mechanism**. Inference mechanism is A* over attestation edges with Glicko-2 significance per arena (per [`35-inference-and-godel.md`](35-inference-and-godel.md) §"The invention"). Query answers come from path traversal, not embedding similarity. Fireflies enable cross-model consensus visualization, conventional embedding-style queries with consensus weighting, polysemy/drift detection, frayed-edge geometric anomaly detection — none of which are the path-from-prompt-to-answer that inference is.

**Why**: Embedding-similarity-based answering reproduces conventional vector-DB / RAG / semantic-search patterns and loses everything that makes the substrate a different kind of AI: per-hop filtering, explanation trace, honest abstention, arena weighting, closed-loop Glicko learning from outcomes. Frayed-edge detection (when a query lands outside any Voronoi consensus cell) IS used by the engine — but as a *signal* to abstain or to flag for curiosity-driven exploration, not as the mechanism for producing an answer.

**Citation**: [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §VII; [`35-inference-and-godel.md`](35-inference-and-godel.md) §"The invention", §"Honest abstention vs hallucination", §"Voronoi consensus on firefly clouds"; `docs/specs/engine/embedding-physicality.md`.

### AP-30: Per-name role contamination

**Failure**: Growing the `TensorRole` enum (or equivalent classification surface) with one value per HuggingFace tensor naming convention — `AttentionQuery` separate from `AttentionKey` separate from `MoeRouter` separate from `LoraA` separate from `RopeFreq`, etc. The enum reaches 40+ values and every new architecture adds more. Code dispatches one decomposer per name, producing 30+ decomposer files that all do variants of the same math.

**Rule**: Tensor classification is `(PrimitiveKind, ArchetypeTuple, TupleSlot)` per [`docs/01-tensor-primitive-spec.md`](../../docs/01-tensor-primitive-spec.md) §I-§II. **PrimitiveKind has 4 values** (`Linear`, `LocalKernel`, `Normalization`, `Lookup`). **ArchetypeTuple has ~13 values** (`AttentionBlock`, `CrossAttentionBlock`, `SwiGluFfn`, `BertFfn`, `MoeRouterBlock`, `LoraDelta`, `ConvResidualBlock`, `ConformerBlock`, `SwinWindowAttn`, `PatchEmbed`, `DetectionHead`, `EmbeddingLookup`, `BnState`, `VaeAttnBlock`). **TupleSlot has ~25 values** (`Q`, `K`, `V`, `O`, `gate`, `up`, `down`, `base`, `lora_A`, `lora_B`, `router`, `expert_*`, etc.). Architecture-specific name mapping is **declarative data** in `TupleResolver` per-architecture tables, not code in decomposers. Decomposer dispatch operates on tuples (compositions), not on per-name singletons.

**Why**: HuggingFace's per-team naming is contamination, not signal. The substrate is a *standard*; decomposers are *parsers* that translate any incoming dialect into the canonical form. Per-name role enum sprawl produces 30+ decomposer files that all do the same math, fragments substrate consensus across architecture boundaries (Llama-King and BERT-King attest to different edges), and forces ongoing maintenance with every new architecture. Collapsing to primitives + tuples gives ~9 decomposer files total and natively cross-architecture / cross-modality / cross-precision consensus accumulation.

**Citation**: [`docs/01-tensor-primitive-spec.md`](../../docs/01-tensor-primitive-spec.md) §0 ("contamination problem"), §I (primitive vocabulary), §II (tuple vocabulary), §III (per-architecture tables), §VI (decomposer collapse).

### AP-31: Sign-throwing decomposers

**Failure**: A decomposer reads tensor values that carry sign (Q^T·K projection, FFN response, embedding cosine), passes `Math.Abs(value)` as the attestation strength, and treats only positive correlation as evidence. Examples in the prior codebase: `TokenAttentionEdgePass`, `TokenCrossEdgePass`, `TokenFfnEdgePass`, `AttentionVoLayerDecomposer` all called `Math.Abs` and discarded sign. Negative correlation (anti-attention, suppression FFN response, antipodal embedding) is load-bearing evidence about what the model has learned to push apart — discarding it makes the substrate half-true.

**Rule**: Glicko-2 already encodes positive vs negative natively via `score` parameter (canonical: 0 = loss, 1 = win) plus per-event `weight`. Decomposers MUST emit sign-aware events: `Score = value > 0 ? 1.0 : 0.0; Weight = Math.Abs(value)`. Edge identity stays the same; mu drifts to consensus position symmetric around the arena's neutral 1500. The substrate distinguishes 4 states: silence (no edge) ≠ wide-sigma (uncertain consensus) ≠ tight-neutral (consensus = "weak relationship") ≠ tight-signed (positive or negative). Synthesizers' mu-to-cell transform must be symmetric around 1500 and produce signed output.

**Why**: Conventional AI training has only positive gradient — negative information lives in regularization or contrastive loss, not in the model's actual recorded knowledge. The substrate's per-edge bidirectional Glicko mu IS the negative information made first-class. Throwing away sign reduces the substrate to "what models think positively" — half the truth, and the wrong half for any anti-pattern detection / antonymy / opposition / suppression query.

**Citation**: [`docs/01-tensor-primitive-spec.md`](../../docs/01-tensor-primitive-spec.md) §V; the existing `inference_outcome_reject` attestation_type (already in seed line 58) demonstrates the pattern.

### AP-32: Per-architecture decomposer files

**Failure**: One decomposer file per HuggingFace naming family — `BertEncoderDecomposer.cs`, `LlamaAttentionDecomposer.cs`, `Qwen3MoeRouterDecomposer.cs`, `BartCrossAttentionDecomposer.cs`, `FluxVaeDecomposer.cs`, etc. Each new architecture spawns new decomposer files. Math duplicates across them with slight variation per naming convention. Cross-architecture attestation accumulation breaks because each decomposer emits to slightly different edge identities.

**Rule**: Per-architecture knowledge lives in **declarative `TupleResolver` tables** (data — per-architecture name-pattern → tuple-slot maps per [`docs/01-tensor-primitive-spec.md`](../../docs/01-tensor-primitive-spec.md) §III), NOT in per-architecture C# files. Decomposer dispatch is `(PrimitiveKind | ArchetypeTuple)` — one pass per primitive (4 files) + one pass per tuple-attestation kind (5 files) + the resolver. New architectures add a table entry; no new code. The same `AttentionBlockTuplePass` handles BERT's `attention.self.{query,key,value}`, Llama's `self_attn.{q,k,v}_proj`, Florence-2 vision's fused `qkv.weight`, and FLUX VAE's `attn_1.{q,k,v}` 1×1-conv-as-attention — because the *math* is identical and the *naming* is data.

**Why**: Per-architecture decomposers fragment the substrate (each file's slight emission variation produces edges with different identities, breaking cross-architecture consensus) and produce unbounded code growth (every new architecture is a new file rather than a table row). The standardization framing — substrate is canonical form for AI — requires that the dialect-to-canonical translation happens in DATA (resolver tables), not CODE (per-dialect decomposers). Once the primitive + tuple decomposer set is correct, ingesting a new architecture is data-only.

**Citation**: [`docs/01-tensor-primitive-spec.md`](../../docs/01-tensor-primitive-spec.md) §0, §III, §VI, §VIII ("substrate as standard").
