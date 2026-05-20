# Model Analysis Passes

> **Authority note (2026-05-09):** Per-role-unit emission for Track-2 transformation tensors is now spec'd as the **layer-type decomposer library** at [`docs/specs/decomposers/layer-type-library.md`](layer-type-library.md), which emits typed attestation edges between existing content entities (NOT phantom per-role-unit entities). The analysis-passes catalog in this file covers the per-tensor analysis surfaces that complement the layer-type decomposers (sparsity profile, weight distribution, eigenvalue spectrum, attention archetype, MoE routing stats, layer similarity, tokenizer mapping, vocab coverage, codec analysis, grammar extraction). The previous per-role-unit-as-entity emission section is deprecated; see the boxed correction below the per-role table.

**Status**: ✅ Implemented. `SafetensorsDecomposer.BuildPassSet()` wires 31 always-on passes (32 with `ModelTextArtifactsPass` when codepoint properties are injected). The Track-2 emission portion is being migrated from phantom per-role-unit entities (deprecated) to attestation edges via the layer-type decomposer library (per spec §V).

The passes transform a discovered AI model into substrate entities, edges, and physicality. Called by the Safetensors decomposer (and any future model-source decomposer) as a composable, re-runnable, checkpointable pipeline. Not related to the 43 **modality** passes in `docs/specs/csharp/analysis-passes.md` — those operate on text/image/audio/video content; these operate on model weights.

The pass catalogue spans both tracks of the two-track ingestion model: **Track 1** (embedding wholesale → Laplacian+GSO → fireflies) via `EmbeddingFireflyPass` — fireflies are POINTZM physicalities attached to existing `word_form` entities, NOT separate `embedding_position` phantom entities (per spec §VII; the firefly side-channel is derived value-add, not the inference mechanism). **Track 2** (transformation weights → typed attestation edges between existing content entities) via the layer-type decomposers spec'd in [`layer-type-library.md`](layer-type-library.md) — Track 2 is the substrate's matmul-replacement: every per-role unit of a transformation tensor manifests as a `model_attention_pattern` / `model_concept_similarity` / `model_ffn_factor` edge between two `word_form` (or other content) entities, with `attestation_type` distinguishing the kind of model evidence.

---

## `IModelAnalysisPass` contract

```csharp
namespace Hartonomous.Decomposers.Safetensors.Passes;

public interface IModelAnalysisPass
{
    /// <summary>
    /// Stable identifier, used for checkpointing and dependency resolution.
    /// Format: "model.{pass_name}" — e.g., "model.svd", "model.vocab_coverage".
    /// </summary>
    string PassId { get; }

    /// <summary>
    /// Pass IDs this pass requires to have completed first, on the SAME model.
    /// Example: VocabCoveragePass depends on TokenizerMappingPass because it
    /// reads the bpe_token entities that TokenizerMappingPass creates.
    /// </summary>
    IReadOnlyList<string> Dependencies { get; }

    /// <summary>
    /// Architecture classes this pass applies to. Empty list = applies to all.
    /// A MoE-specific pass returns [DeepseekV2ForCausalLM, Qwen2MoeForCausalLM, ...].
    /// The orchestrator skips passes whose architecture filter doesn't match.
    /// </summary>
    IReadOnlyList<string> AppliesToArchitectures { get; }

    /// <summary>
    /// Run the pass. Writes entities/edges/physicality via the provided batch.
    /// Must be deterministic on inputs — same model + same substrate state → same
    /// output, bit-for-bit. Throws on failure; the orchestrator halts the model.
    /// </summary>
    Task RunAsync(ModelPassContext context, IIngestionBatch batch, CancellationToken ct);
}
```

### `ModelPassContext`

Contains everything a pass needs about the model being analyzed, with origin metadata *on the context, not inside the content*. Passes call `context.ComputeContentHash(...)` helpers that strip origin before hashing (see § *Canonical signatures* below).

```csharp
public sealed record ModelPassContext(
    ModelSourceHandle Source,                 // registry/publisher/model_slug/revision — placement only
    ModelArchitectureHandle Architecture,     // architecture entity (hashed by arch content alone)
    IReadOnlyList<TensorHandle> Tensors,      // tensor entities already created + classification
    IComputeFacade Compute,                   // IoC'd reference to Hartonomous.Core.Compute
    IReferenceTableReader RefTables,          // architecture_class, tensor_role, edge_type, etc.
    string CheckpointKey);                    // for per-pass per-model checkpoint state
```

### Orchestration

The decomposer runs passes as a DAG, topologically sorted by `Dependencies`. Per-model checkpoint state records which passes have completed for which `(model_source_id, pass_id)` pair. A crash mid-model resumes at the next unrun pass on the next startup — no duplicated work, no partial-state corruption. Checkpoint key is `{model_source_id}.{pass_id}` (structured, not a jammed string — stored as two columns in a `model_pass_checkpoint` table).

Failure isolation: a pass that throws halts the model but does not halt other models. The orchestrator logs, marks the model as `pass_failed`, and proceeds to the next model. Failed-pass models can be re-driven with `phase-runner phases resume --decomposer safetensors --retry-failed`.

---

## Canonical signatures (entity-hash-content-only)

Every pass that creates an entity must hash **content only** — never filename, publisher, revision, tensor name, ordinal, or any other placement. Placement lives on edges (`has_tensor`, `in_model`, `has_source`, `at_layer_index`, …).

A canonical signature is a stable, ordered byte serialization of the content fields that define the entity's identity. Passes use the helpers in `ModelPassContext.Compute` to build signatures:

```csharp
public interface ICanonicalSignatureBuilder
{
    void WriteInt32LE(int value);
    void WriteInt64LE(long value);
    void WriteDouble(double value);                // IEEE 754 big-endian for portability
    void WriteUtf8(ReadOnlySpan<char> value);      // length-prefixed
    void WriteBytes(ReadOnlySpan<byte> value);     // length-prefixed
    void WriteHash(ReadOnlySpan<byte> blake3_32);  // length-prefixed
    byte[] Finalize();                             // BLAKE3 of the serialized fields
}
```

The builder prepends a 4-byte kind tag (`"arch"`, `"tens"`, `"svds"`, `"eigs"`, `"spar"`, `"fire"`, `"toke"`, `"atnh"`, …) so signatures of different kinds can never collide even if their field bytes overlap. Kind tags are enumerated in `docs/specs/native/4d-type-and-index.md`.

**Prohibited signature inputs** — will fail the pass hash audit in CI:

- Publisher name, registry name, model slug, revision hash
- File path, filename, shard index
- Tensor name, layer index (unless the layer index IS content — e.g., `AttentionArchetypeEntity` for *a specific layer's* head pattern is per-(architecture, layer, head) and the layer is content, not placement)
- Ordinal position within a list
- Timestamp, ingestion session id

**Permitted signature inputs** — examples:

| Entity kind | Signature fields |
|---|---|
| `model_architecture` | architecture_class_id, hidden_size, num_layers, num_heads, num_kv_heads, vocab_size, intermediate_size, rope_theta, tie_embeddings |
| `tensor` | dtype code, shape[], BLAKE3 of raw tensor bytes |
| `svd_spectrum` | parent tensor hash, top-k singular values packed as f64 LE |
| `sparsity_profile` | parent tensor hash, histogram bucket edges, bucket counts |
| `weight_distribution` | parent tensor hash, mean, variance, kurtosis, min, max |
| `attention_archetype` | architecture hash, layer index, head index, archetype vector hash |
| `bpe_token` | tokenizer model code, token string bytes |
| `vocab_entry` | tokenizer model code, token id, token string bytes |

Two models with bit-identical weights → same `tensor` hashes. Two architectures with identical structure → same `model_architecture` hash (even different publishers). Same singular value spectrum computed over identical weights → same `svd_spectrum` hash — substrate corroborates instead of duplicating.

---

## Pass catalogue

All compute goes through `context.Compute` (the `Hartonomous.Core.Compute.*` facade). No pass allocates numerical arrays larger than `context.MaxWorkingSetBytes`, chunking via facade primitives when needed.

### Track 1 — embedding fireflies

#### `EmbeddingFireflyPass`

- **Id**: `model.embedding_fireflies`
- **Depends on**: (none)
- **Applies to**: all
- **What it does.** For each Track-1 tensor (token embedding, position embedding, token-type embedding), run k-NN → Laplacian eigenmap → Gram-Schmidt → 4D firefly coordinate via `Compute.Ingestion.KnnCosineGraph` + `Compute.Ingestion.SparseSymEigs` + `Compute.Common.GramSchmidt`.
- **Entities created.** One `bpe_token` entity per row. Signature: tokenizer model + token bytes (not ordinal). Content-addressing dedupes the same token across models.
- **Physicality.** 4D S³ firefly point per (bpe_token, model) — different models see the same token at different S³ positions; the firefly model preserves per-model placement while sharing the token identity.
- **Edges.** `has_embedding_in(bpe_token, model_architecture)` carrying the firefly placement.

### Track 2 — weight decomposition

#### `SvdPass`

- **Id**: `model.svd`
- **Depends on**: (none)
- **Applies to**: all architectures containing 2D weight matrices (Q/K/V/O projections, FFN gate/up/down, classifier heads, LoRA A/B, etc.)
- **What it does.** Top-k singular values per decomposed weight matrix via `Compute.Ingestion.Svd.F64`. Stores singular spectrum as content-addressed entity; the decay curve encodes significance density of the transformation.
- **Entities.** `svd_spectrum` per tensor — signature is (parent tensor hash, truncation k, singular values). Identical weights across two models produce one entity, two `has_spectrum` edges.
- **Edges.** `has_spectrum(tensor, svd_spectrum)`, `spectrum_element(svd_spectrum, rank_index)` where rank_index is the ordinal on the edge, never inside the entity hash.

#### `EigenvaluePass`

- **Id**: `model.eigenvalues`
- **Depends on**: (none)
- **Applies to**: square weight matrices (attention QK projections when combined, LayerNorm scale vectors stacked into symmetric forms).
- **What it does.** Eigenvalue spectra for matrices where the transformation is naturally interpretable as an operator. Uses `Compute.Ingestion.SparseSymEigs` for large symmetric forms, dense eigensolve via `Svd` pathway for small cases.
- **Entities.** `eigenvalue_spectrum` — signature (parent tensor hash, k, eigenvalues).

#### `SparsityAnalysisPass`

- **Id**: `model.sparsity`
- **Depends on**: (none)
- **Applies to**: all.
- **What it does.** Per-tensor sparsity profile: fraction of near-zero entries, magnitude histogram (log-scale buckets), block-sparsity (2:4 / 4:8 patterns where applicable). Drives Track 2's functional sparsity filter: weights that pass Lottery Ticket thresholds become edges; weights below are not stored (Law #11).
- **Entities.** `sparsity_profile` per tensor. Signature: (parent tensor hash, bucket edges, bucket counts, near-zero fraction).
- **Note.** This pass reports the *pattern* of sparsity in the input. It does not *create* sparsity; it exposes it for downstream passes to use when deciding which weight patterns cross the significance threshold.

#### `WeightDistributionPass`

- **Id**: `model.weight_distribution`
- **Depends on**: (none)
- **Applies to**: all.
- **What it does.** Mean, variance, skew, kurtosis, min, max per tensor and aggregated per layer.
- **Entities.** `weight_distribution` — signature (parent tensor hash, statistic values in canonical order).

#### `ActivationRangePass`

- **Id**: `model.activation_range`
- **Depends on**: `model.weight_distribution`
- **Applies to**: decoder / encoder blocks (not embedding-only).
- **What it does.** Estimate activation range statistics from weight norms (L2, L∞) combined with architectural constants (hidden size, head dim). No actual forward pass is run — this is purely weight-derived. Used by inference-time arena code to set per-layer significance scaling.
- **Entities.** `activation_range` — signature (parent tensor hash, estimated min, max, L2, L∞).

#### `AttentionArchetypePass`

- **Id**: `model.attention_archetype`
- **Depends on**: `model.svd`
- **Applies to**: architectures with standard multi-head self-attention (Llama, Qwen, BERT, DETR, GPT-* lineage).
- **What it does.** Classify what relation each attention head encodes (syntax, positional, coreference, semantic-role, lexical-identity, global) by running a fixed battery of probes on the (Q·Kᵀ)/√d pattern matrix for a canonical set of inputs. The probes are deterministic; the "canonical set" is a frozen seed corpus stored as substrate-owned bytes hashed into the pass's content.
- **Entities.** `attention_archetype` — signature (model_architecture hash, layer index, head index, archetype classification vector hash). Layer and head indices ARE content for this entity — they identify which head of which architecture encodes this archetype, not "where in the file it was stored."
- **Edges.** `encodes_archetype(tensor, attention_archetype)` with role `q_proj`/`k_proj`/`v_proj`/`o_proj`.

#### `MoERoutingStatsPass`

- **Id**: `model.moe_routing`
- **Depends on**: (none)
- **Applies to**: MoE architectures (DeepseekV2, Qwen2Moe, Mixtral, …)
- **What it does.** Router weight analysis — per-layer expert-utilization distribution, routing entropy, dead-expert detection. Uses static weight inspection only; no token-routing simulation.
- **Entities.** `moe_routing_profile` — signature (model_architecture hash, layer index, expert count, utilization vector packed f64 LE).

#### `LayerSimilarityPass`

- **Id**: `model.layer_similarity`
- **Depends on**: `model.svd`
- **Applies to**: decoder / encoder blocks.
- **What it does.** Cross-layer weight similarity via SVD subspace alignment (principal angles between top-k left singular spaces). Identifies layers doing redundant work — input to Recomposer's distillation target-size selection.
- **Entities.** `layer_similarity_pair` — signature (model_architecture hash, layer_i, layer_j, principal angle vector). Both layer indices are content for this pairwise entity.

### Track 1/2 — tokenizer & vocab

#### `TokenizerMappingPass`

- **Id**: `model.tokenizer`
- **Depends on**: (none)
- **Applies to**: all.
- **What it does.** Parse `tokenizer.json` / `vocab.json` / sentencepiece model. Canonicalize each token via the shared UAX #29 primitives (see `docs/specs/decomposers/tokenizers.md`, task #32). Create `bpe_token` entities keyed by tokenizer model code + token bytes (content only). Link to substrate codepoint entities (seeded by UCD/UCA in M5a) via `composed_of_codepoints` edges.
- **Entities.** `bpe_token`, `vocab_entry`.
- **Edges.** `has_vocabulary(model_architecture, vocab_entry)` with token id on the edge; `composed_of_codepoints(bpe_token, codepoint)` for multi-codepoint tokens.

#### `VocabCoveragePass`

- **Id**: `model.vocab_coverage`
- **Depends on**: `model.tokenizer`
- **Applies to**: all.
- **What it does.** For each vocab entry in this model, resolve it against substrate lexical entities (WordNet lemmas, Wiktionary entries, UD tokens). Compute coverage statistics: fraction of vocab that matches seed lexical entities, fraction that matches code-domain or math-domain specialized tokens, fraction genuinely novel. Emits `covers_lemma` edges for matches.
- **Entities.** `vocab_coverage_profile` per (model_architecture) — signature (model_architecture hash, coverage statistics in canonical order).

### Audio-specific

#### `CodecAnalysisPass`

- **Id**: `model.codec_analysis`
- **Depends on**: (none)
- **Applies to**: neural codec architectures (Encodec, Fish Speech codec, SNAC, DAC).
- **What it does.** VQ codebook utilization and entropy; quantization residual statistics; codebook-vector S³ projection via `Compute.Common.SuperFibonacci` so codec codebooks become traversable via the same geometric primitives as any other embedding.
- **Entities.** `codec_codebook` (per VQ stage) — signature (model_architecture hash, stage index, codebook dim, codebook hash). `codec_codevector` per codeword — signature (codebook hash, code index, vector bytes).

### Grammar emergence

#### `GrammarExtractionPass`

- **Id**: `model.grammar_extraction`
- **Depends on**: `model.attention_archetype`
- **Applies to**: text-generative architectures.
- **What it does.** Inspect attention archetypes flagged as structural (clause boundaries, nesting, phrase brackets). Cluster them across layers and heads. For clusters that exceed an extraction threshold, synthesize a candidate Tree-sitter grammar fragment (`.scm` pattern). The fragment is an entity; it enters the arena against hand-authored grammars with a provisional significance seeded by the archetype cluster's coherence.
- **Entities.** `grammar_fragment` — signature (model_architecture hash, archetype cluster hash, fragment text UTF-8 bytes). Same fragment derived from two models → one entity, two corroborating `derives_from` edges.

---

## Per-role unit emission — DEPRECATED FRAMING (see corrected vision below)

> **Architectural correction (2026-05-08; see [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §III, §V, §XII):** The "one entity per meaningful unit" framing previously documented in this section is the phantom-decomposition shape. **Per-role units of Track 2 transformation tensors manifest as typed attestation EDGES between existing content entities (typically two `word_form` tokens), NOT as synthetic per-role-unit entity types.** The phantom entity types previously listed here (`ffn_neuron`, `embedding_position`, `attention_component`, `logit_projection`, `attention_pattern`, `moe_route_direction`, `moe_expert_neuron`, `object_query_slot`, `class_projection`, `bbox_projection`, `vision_feature_direction`, `modality_basis_vector`, `lora_component`, `conv_filter`, `diffusion_component`, `conformer_component`, `audio_codec_filter`) are deprecated and are documented in spec §XII as the phantom debt removal list. The corresponding `*Pass` classes in `src/Hartonomous.Decomposers/Safetensors/Passes/` are deprecated and on the removal path.
>
> The replacement is the **layer-type decomposer library** specified in [`docs/specs/decomposers/layer-type-library.md`](layer-type-library.md). Each universal layer decomposer (`AttentionQkvLayerDecomposer`, `AttentionVoLayerDecomposer`, `FfnLayerDecomposer`, `EmbeddingLayerDecomposer`, `LmHeadLayerDecomposer`, `LayerNormLayerDecomposer`, `MoeRouterLayerDecomposer`, `MoeExpertLayerDecomposer`, `LoRAAdapterLayerDecomposer`) and specialist layer decomposer (`CrossAttentionLayerDecomposer`, `ConvLayerDecomposer`, `ViTPatchAttentionLayerDecomposer`, `CodecRvqLayerDecomposer`, `DetectionHeadLayerDecomposer`, `DiffusionUnetLayerDecomposer`) emits typed attestation edges between existing content entities, with `attestation_type` (per `sql/schema/seed/attestation_type.sql`) on the rating event distinguishing the kind of model evidence. Cross-model corroboration accumulates as separate `attestation_type`-distinguished events on the same edge hash. See AP-25 in `.claude/rules/45-anti-patterns.md` for the detection-and-rejection guidance.
>
> The working template for what every layer-type decomposer should look like: [`src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`](../../../src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs).
>
> The shared sparse-recording mechanism (per-tensor adaptive noise floor via `PerRowContentPass.ComputeAdaptiveNoiseFloor`, threshold-then-hash for cross-model dedup on signal not jitter, skip-entirely-jitter rows) is correctly implemented in the deprecated phantom passes and is preserved in the new layer-type decomposer pattern — only the emission target changes (attestation edges between content entities instead of phantom per-role-unit entities). See spec §VIII.

---

## Determinism obligations

Every pass must:

- Use `context.Compute` primitives for every nontrivial numerical operation. No managed-code loops for matrix math.
- Pass a fixed seed (derived from `ModelPassContext.CheckpointKey` via BLAKE3) to any primitive that accepts a seed. Same context → same seed → same answer.
- Build entity hashes via the canonical signature builder; never `string.Join`, never `$"{a}|{b}|{c}"` interpolation, never truncated hashes.
- Declare any input file dependencies by hash. If the pass reads the tokenizer JSON, the tokenizer JSON's BLAKE3 becomes part of the `TokenizerMappingPass` output entity's signature so that a changed tokenizer produces a different entity.

A CI determinism test runs the full pass catalogue twice on a fixed small model (MiniLM-L6-v2) in isolated DBs and asserts bitwise-identical entity hash sets.

---

## Cross-references

- `docs/specs/decomposers/safetensors.md` — high-level two-track ingestion model; this spec is the implementation detail of Track 2 + tokenizer link-up.
- `docs/specs/decomposers/tokenizers.md` — shared UAX #29 text-segmentation primitives (task #32) that `TokenizerMappingPass` depends on.
- `docs/specs/csharp/compute-facade.md` — the facade every pass calls.
- `docs/specs/engine/embedding-physicality.md` — math behind `EmbeddingFireflyPass`.
- `docs/specs/csharp/analysis-passes.md` — *separate* 43-pass catalogue for text/image/audio/video modalities (not related to model analysis).
- `docs/architecture.md` Law #6 (determinism), Law #11 (sparsity), and the entity-hash-content-only rule (`CLAUDE.md` § *Native Interop*).
