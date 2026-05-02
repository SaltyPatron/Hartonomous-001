# Recomposer Contract

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers writing or maintaining recomposers for any output format. Critically: this is the load-bearing engineering for refinement-as-service and Laplace originals.

---

## What a recomposer is

A recomposer is a function from `(target architecture spec, substrate selection recipe) → output bytes`. The output is a complete, valid artifact in the target consumer's format: safetensors directory, image file, audio file, document, etc.

Recomposers do not invent. They project substrate state onto the target format. Every byte they emit traces back to substrate edges traversed by the recipe.

Recomposition is the dual of decomposition. Where decomposers turn bytes into substrate compositions, recomposers turn substrate state into bytes. The substrate is the parent body; recomposers are the membrane that produces daughters.

## The contract surface

```csharp
public interface IRecomposer<TOutput> {
    string OutputFormat { get; }                   // "safetensors", "image_png", "audio_wav", ...
    string DisplayName { get; }
    Task<RecomposerCapabilities> Validate(RecomposerSpec spec, CancellationToken ct);
    Task<TOutput> Recompose(RecomposerSpec spec, ISubstrateReader substrate, IProgressReporter reporter, CancellationToken ct);
}

public record RecomposerSpec(
    object TargetFormat,           // format-specific spec object (e.g., SafetensorsArchSpec for safetensors)
    string SubstrateRecipe,        // SQL-or-DSL filter recipe
    int? ProvenanceFilter,         // optional: restrict to one provenance
    double SignificanceFloor,      // below-threshold = zero
    string OutputPath
);
```

Each output format has a recomposer. The recomposer's job:
1. Validate the target format spec is achievable given current substrate state.
2. Walk substrate edges per the recipe, projecting their state onto the target format.
3. Emit bytes that are a valid artifact in the target format.

## Two operating modes

### Mode 1 — Refinement: target architecture matches an ingested model

The customer's input safetensors was ingested earlier; the substrate has its tensors decomposed into edges with sub-provenance like `huggingface_model:llama4-maverick`. The recomposer reads the original model's `config.json`-equivalent metadata (preserved during ingestion as substrate metadata about the model's identity), walks the substrate edges tagged with that model's sub-provenance, and projects to a safetensors output with identical architecture.

Refined values incorporate cross-source corroboration that happened automatically during substrate accumulation. Where Llama-4-Maverick's original attention weight at `(layer 5, head 7, row 234, col 891)` had value 0.31 BUT the substrate has accumulated agreement from WordNet + UD + 3 other LLMs on the underlying relationship, the refined value is the consensus mu projected to scalar — likely higher than 0.31 because of corroboration. Where Llama's original weight had no cross-source corroboration, the refined value falls toward zero (below threshold).

**Output:** identical architecture, identical tokenizer, identical config.json semantics. Refined per-position values. Drop-in replacement.

### Mode 2 — Origination: target architecture is novel (Laplace originals)

Anthony specifies a novel architecture. The recomposer reads the architecture spec, walks substrate edges (potentially across all provenance, weighted by the recipe's arena filter), and projects them onto the target architecture's tensor shapes. The output is a NEW model whose architecture doesn't correspond to any single ingested teacher.

**Output:** Laplace-Linguistics-7B, Laplace-Coder-30B-MoE, Laplace-Custom-{customer-spec} — fresh safetensors with provenance traceable to the substrate's accumulated state.

The two modes share infrastructure. The difference is in the architecture spec source:
- Mode 1 reads spec from the substrate's stored metadata about an ingested model.
- Mode 2 reads spec from the customer's submitted RecomposerSpec.

The recomposer's projection logic is the same.

## The projection problem

The substrate's edges encode relationships with arena-mu-significance. The output formats expect specific shaped tensor blocks with float values per position. Projection bridges these representations.

For each tensor in the target architecture, the recomposer:

1. **Identifies the entity vocabulary.** The target architecture's tokenizer specifies a vocabulary of N tokens. Each vocabulary entry corresponds to a substrate composition entity (text composition for text models, image-region for vision, etc.).

2. **For each tensor position `(layer, role, row, col)`:** issues a SQL query against `substrate.edge` filtered by `(layer, role)` metadata for the target's source-provenance, between vocabulary entry `row` and vocabulary entry `col`. The query joins `edge_significance` in the relevant arena to get current consensus mu.

3. **Applies threshold.** Below `significance_floor` → 0. Above → projection function maps mu to scalar weight.

4. **Bulk-fetch optimization.** Single query per matrix, not per element: `SELECT vocab_a, vocab_b, mu FROM beaten_path_edges WHERE (layer, role) = (L, 'attn_q') AND mu > floor`. Returns sparse rowset. Recomposer materializes into dense matrix at correct (row, col) positions.

5. **Serializes per format.** For safetensors: write 8-byte header size, JSON header with tensor metadata, raw tensor bytes per dtype.

## Projection function (the actual numerical mapping)

The naive projection is `weight = mu / 3000` (Glicko mu in [0, 3000+] range, mapping to [0, 1+] scalar). This is too crude for production refinement; quality depends on a proper projection function.

Per-tensor-role projection rules (initial):

| Tensor role | Projection |
|---|---|
| Embedding (vocab × hidden) | Per-token row from substrate `physicality(embedding_firefly)` aggregated across all source provenance. Dimension reduction from 4D back to native hidden via stored full-resolution embedding (preserved at ingestion). |
| Attention Q/K/V | `mu_consensus * sign(original_weight) / normalization` for sparse positions; zero for below-threshold. Normalization preserves spectral characteristics of the matrix. |
| Attention O | Similar to Q/K/V; substrate edges for output projection are typed as `beaten_path` with `output_proj` role tag. |
| FFN gate / up / down | `transformation` edges' mu projected with role-specific normalization (gate is multiplicative-contribution, up is expansion, down is reduction). |
| LM head | `hidden_to_token` edges; analogous to embedding (vocab × hidden) but with output-projection norm. |
| Layer norm | RMS-norm scale parameters: substrate stores per-layer norm scale evidence; recomposer reads as a small (hidden,) tensor. |
| Position encoding | RoPE: not stored as substrate edges; recomposer applies target's chosen scheme (RoPE base, NTK scaling) at recompose time. |
| Token embedding (input) | Same as Embedding above. |
| MoE router | Per-input routing pattern edges; recomposer projects to (hidden × num_experts) router matrix. |

The projection rules per tensor role are themselves a small body of code (not a learned model). Each rule is independently testable. Each rule's correctness is verified by golden tests: ingest a known model, recompose, compare against original on a curated test set, expect refined to match or beat.

## Sparsity from significance threshold

Recomposition's primary efficiency comes from `significance_floor`. Below-threshold positions are zero. The output safetensors file:

- Same on-disk shape as the original (target architecture is preserved).
- Most positions are zero.
- Sparse-tensor-aware compression (e.g., gzip on disk, sparse-tensor inference paths in vLLM/llama.cpp) materially reduces effective size and inference cost.

For Mode 1 (refinement), this means refined-Llama-4-Maverick is structurally identical to original Llama-4-Maverick on disk but operationally smaller — most weights zeroed. Compress to disk; decompress at load; inference paths short-circuit on zeros where supported.

For Mode 2 (origination), sparsity reflects substrate density: the substrate has more attestation in some regions of vocabulary × hidden than others; the resulting tensors are dense where the substrate is dense and sparse where it isn't.

## Cross-source corroboration's effect on refinement

This is the mechanism that makes refinement actually superior to the original model:

1. **Original Llama-4-Maverick weight encodes one source's signal.** The training process landed at value 0.31 for `(layer 5, attn_q, row 234, col 891)`. That's Llama's training-data attestation.

2. **Substrate has accumulated other sources' attestations.** WordNet's `hypernym` edges, UD's `dep_*` edges, Wiktionary's translation edges, other ingested LLMs' attentions — all converge on related substrate edges between vocabulary entries.

3. **Cross-source Glicko consensus.** Where multiple sources attest the same underlying relationship (e.g., between vocabulary entries 234 and 891 in semantic_relevance arena), arena mu aggregates the attestations. The consensus mu is higher than any single source's attestation (Glicko increases mu under corroboration).

4. **At refinement time, the recomposer reads consensus mu, not Llama's original value.** The output weight reflects the substrate's accumulated state, which includes Llama's contribution PLUS everything else.

5. **Where Llama disagreed with corroborating sources** (Llama's hallucinated patterns), Glicko pushed mu DOWN under contradiction. The refined weight is below threshold → zero. Llama's hallucinations don't make it into the refined export.

6. **Where Llama agreed with corroborating sources** (Llama's correct patterns), Glicko pushed mu UP under corroboration. The refined weight is higher than original → cleaner signal. Llama's signal IS PRESERVED — it's reinforced by what other sources attest.

This is why refinement is automatic. The substrate did the cross-source resolution at ingestion time via Glicko mechanics. Export demonstrates the resolved state.

## Output formats

Each format has a dedicated recomposer module:

| Recomposer | Format | Customer use |
|---|---|---|
| `SafetensorsRecomposer` | safetensors directory (config.json + tokenizer.json + model.safetensors[+ shards]) | Refinement-as-service, Laplace originals |
| `TextRecomposer` | UTF-8 string | Inference output for text queries |
| `WaveformRecomposer` | WAV / FLAC | Audio output (TTS, music generation) |
| `ImageRecomposer` | PNG / JPEG | Image output (diffusion-derived generation) |
| `JsonRecomposer` | JSON | Structured response data |
| `TreeSitterRecomposer` | source code (per language) | Code generation output |
| `Markdown/HtmlRecomposer` | document formatted bytes | Document generation |

All recomposers share the projection-from-substrate primitive but specialize in format-specific byte emission.

## Safetensors recomposer specifics

Per the safetensors format spec:

```
[8-byte little-endian uint64: header size N]
[N bytes: JSON header]
[remaining bytes: tensor data blocks back-to-back]
```

The JSON header is:
```jsonc
{
    "tensor_name_1": {
        "dtype": "F32",
        "shape": [4096, 11008],
        "data_offsets": [0, 180355072]
    },
    "tensor_name_2": { ... },
    "__metadata__": {
        "format": "pt",
        "framework": "pytorch",
        "license": "...",
        "hartonomous_substrate_state": "<substrate state hash>",
        "hartonomous_recomposer_version": "1.0",
        "hartonomous_recipe_id": "<recipe content hash>",
        "hartonomous_provenance_chain": "<encoded chain of source provenance>"
    }
}
```

The substrate-specific `__metadata__` keys provide audit chain:
- `hartonomous_substrate_state` is the hash of the substrate's state at recompose time (a Merkle root over all ingested provenance).
- `hartonomous_recipe_id` is the content hash of the recipe that drove the recompose.
- `hartonomous_provenance_chain` lets external auditors reconstruct which source attestations contributed which positions.

**Recomposer process for safetensors:**

```
1. Read target arch spec (layer count, dimensions, etc.)
2. Compute output tensor list with shapes and dtypes
3. For each tensor:
    a. Identify substrate edges contributing to this tensor (per role/layer mapping)
    b. Bulk-fetch significance: SELECT vocab_a, vocab_b, mu FROM ... WHERE filter
    c. Materialize into a sparse-format tensor (dict-of-keys then to dense)
    d. Apply projection function (mu → scalar weight per role-specific rule)
    e. Threshold: below significance_floor → zero
    f. Compute byte offsets given dtype and shape
4. Build header JSON with all tensor metadata
5. Write 8-byte header size
6. Write header JSON bytes
7. Write each tensor's bytes in offset order
8. Optionally split into shards (model-NNNNN-of-MMMMM.safetensors) per safetensors convention for large models
9. Emit auxiliary files: config.json (architecture spec), tokenizer.json (substrate tokenizer state), tokenizer_config.json, special_tokens_map.json, generation_config.json
```

**Shard size:** standard practice splits at ~5GB per shard. For a 30GB model, ~6 shards.

**Compatibility:** the output safetensors loads with HuggingFace `transformers`, vLLM, llama.cpp, TGI, or any other inference stack supporting safetensors. The substrate-specific `__metadata__` keys are non-breaking — they're simply additional metadata that consumers can ignore.

## Determinism

Recomposition is deterministic per Substrate Law #6. Given:
- The same substrate state (Merkle root)
- The same target architecture spec
- The same recipe
- The same significance floor
- The same recomposer version

The output safetensors is byte-identical. Two re-runs of the same recompose produce the same file.

This means: customers can reproduce a refined model by replaying the recipe against a substrate-state snapshot. Snapshots can be archived; recipes can be archived; reproducibility is preserved through time.

## Validation gates

A recomposer is production-ready when:

1. **Determinism gate.** Run recompose twice with same inputs; verify byte-identical output.
2. **Loadability gate.** Output loads with `transformers.AutoModelForCausalLM.from_pretrained(...)` (or appropriate consumer library) without errors.
3. **Sample-prompt gate.** Loaded model generates coherent output for a representative-sample prompt set. No NaN, no infinity, no crash.
4. **Architecture-preservation gate.** For Mode 1, output's config.json matches input's config.json byte-for-byte (modulo permitted metadata additions).
5. **Sparsity gate.** Output's nonzero positions count matches expected (substrate edges above threshold, plus required defaults like layer norm).
6. **Provenance gate.** Output's `__metadata__.hartonomous_provenance_chain` is verifiable: every position in the output should trace to substrate edges in the chain.
7. **Round-trip gate (Mode 1).** For an ingested model M, refine to M'. Compare `quality(M)` and `quality(M')` on standard benchmarks for the model's domain. M' should match or exceed M; if it doesn't, the recomposer's projection function needs work.

These gates are blocking for first commercial deliverable. Until 1–6 pass, the recomposer doesn't ship; until 7 passes, refinement-as-service doesn't claim "FAR SUPERIOR."

## Cross-references

- Substrate laws governing recomposition: `10-architecture/01-substrate-laws.md` (Laws 6, 9, 11)
- The decomposer counterpart: `10-architecture/05-decomposer-contract.md`
- The architecture pillars driving projection design: `10-architecture/00-overview.md`
- Per-format recomposer details: `20-technical/07-recomposer-implementations.md`
- Recomposer checklist: `40-process/checklists/01-recomposer-checklist.md`
- The cognitive surface that exposes recomposition as SQL: `10-architecture/08-cognitive-surface.md`
- The product line that depends on these recomposers: `00-business/01-product-line.md`

## External references

- safetensors format specification: <https://github.com/huggingface/safetensors>, <https://huggingface.co/docs/safetensors>
- HuggingFace `transformers` model loading conventions: <https://huggingface.co/docs/transformers/main_classes/model>
