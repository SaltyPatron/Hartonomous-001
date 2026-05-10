# Embedding Layer Synthesis — Fireflies as the Substrate's Embedding

**Status:** Canonical for `EmbeddingLayerSynthesizer`. Supersedes the inverse-Laplacian-eigenmap approach previously in `docs/00-substrate-spec.md §VI` Mode 2 description.

**Authority:** Architectural correction landed 2026-05-09. The substrate's embedding for a token IS the consensus over its firefly cluster — NOT a 4D approximation of the source's hidden_dim row that we then try to invert back. Forward-only operation; no inverse problem.

**Reciprocal of:** `EmbeddingLayerDecomposer` (Phase A.1) — emits per-(model, token) POINTZM fireflies attached to existing word_form entities; this synthesizer reads them.

---

## The architectural reframing

Previous framing (lossy and complex): "Given a 4D firefly POINTZM, recover the original hidden_dim embedding row" — fundamentally lossy because we discarded hidden_dim − 4 bits at projection time. Required either per-source basis storage (Strategy B) or PCA fallback over attestation participation (Strategy A).

Corrected framing (lossless forward expansion): "The substrate's embedding for a token IS the consensus over its firefly cluster. Expand the consensus to target hidden_dim via deterministic basis." Forward-only; no inverse needed.

The substrate is **4D-native** for embedding. Each token's embedding is the consensus across all ingested models that contributed a firefly. Cross-model agreement tightens the consensus (Lottery Ticket compound effect — see [`lottery-ticket-foundations.md`](lottery-ticket-foundations.md)). When a target architecture requires hidden_dim H, the synthesizer expands 4D → H via a deterministic basis chosen at export time.

---

## Two consensus aggregation modes (cluster shape determines which)

The choice between centroid-based and shape-archetype-based consensus depends empirically on firefly cluster geometry. Both are exact closed-form using existing substrate primitives.

### Mode 1 — Centroid consensus (when clusters are tight)

For a target token, query its firefly cluster:
```sql
SELECT geom FROM substrate.physicality
WHERE entity_hash = :token_hash
  AND physicality_type_id = (SELECT id FROM substrate.physicality_type WHERE code = 'embedding_firefly');
```

Compute the consensus 4D position:
```sql
SELECT substrate.st_4d_centroid(geom) FROM ... -- as above
```

The result is a single POINTZM in 4D — the substrate's authoritative embedding for that token.

**Cluster tightness check** (per spec §X analytics):
- Compute cluster radius (max distance from each firefly to centroid)
- Compute median pairwise distance among neighbor tokens' fireflies
- If `cluster_radius / inter_token_distance < tightness_threshold`: cluster is tight → centroid is reliable
- Otherwise: fall through to Mode 2

### Mode 2 — Shape-archetype consensus (when clusters scatter)

When fireflies for a token scatter across S³ rather than clustering tightly, the centroid is unreliable but the SHAPE of the scatter carries information. Use Hausdorff or Fréchet distance to match the cluster shape against a known archetype set:

- **Hausdorff distance** between two firefly clusters: max distance from any point in cluster A to nearest point in cluster B. Works on unordered point sets (MULTIPOINTZM).
- **Fréchet distance** on ordered firefly trajectories (ordered by ingestion timestamp or model trust prior): shape-aware distance handling cluster structure.

Substrate primitives already exist: `substrate.st_4d_hausdorff_distance`, `substrate.st_4d_frechet_distance`. Same primitives that frayed-edge detection (per spec §X) and analogy completion (per spec §VIII) use.

For shape-archetype synthesis: cluster the firefly clusters of all tokens via Hausdorff distance into a small archetype set; for each target token, identify its closest archetype and use the archetype's representative embedding for synthesis.

---

## Forward 4D → hidden_dim expansion

Given a 4D consensus point `c = (cx, cy, cz, cw)` and target `hidden_dim` H, expand to an H-dim embedding row via one of three deterministic strategies (caller picks per the recipe):

### Strategy E1 — Honest abstention padding (simplest)

```
embedding_row[0] = cx
embedding_row[1] = cy
embedding_row[2] = cz
embedding_row[3] = cw
embedding_row[4..H-1] = 0  // honest abstention: substrate doesn't carry these bits
```

The H − 4 zero positions are exactly where source models carried gradient-descent noise per LTH; honest abstention makes that explicit. Target architecture's attention/FFN layers (also synthesized from substrate consensus, similarly 4D-meaningful) operate on these sparse rows. Cells the substrate has no consensus for stay zero.

### Strategy E2 — Orthonormal basis expansion

```
basis = deterministic_orthonormal_basis(H)  // e.g., RoPE-frequency-aligned, or Hadamard, or DCT
embedding_row = basis × [cx, cy, cz, cw, 0, 0, ..., 0]^T  // padded to H
```

The 4D content is preserved (rotation-invariant) but distributed across H dimensions via a basis the target architecture finds congenial. Useful when the target architecture's downstream layers (attention QK heads, RoPE rotation) expect content distributed across the row rather than concentrated in the first 4 positions.

### Strategy E3 — Cross-attestation expansion (most expressive, more substrate state needed)

Use the per-token attestation graph (`model_input_embedding`, `model_concept_similarity` edges to other tokens) as additional signal beyond the 4D centroid. For each non-substrate dimension i ∈ [4, H), derive a value from the token's attestation participation pattern (e.g., projected via a Hadamard matrix indexed by attestation neighbor hashes, or via PCA over the attestation neighborhood).

Strategy E3 is the most expressive but consumes more substrate state per export. Strategies E1 and E2 are sufficient for most use cases.

---

## Implementation surface

```csharp
public sealed class EmbeddingLayerSynthesizer : ILayerTypeSynthesizer
{
    public bool Handles(TensorRole role) => role is
        TensorRole.TokenEmbedding or TensorRole.PositionEmbedding or
        TensorRole.PositionEmbedding2D or TensorRole.TokenTypeEmbedding;

    public async Task<byte[]> SynthesizeAsync(
        TargetTensorSpec target,
        SubstrateAttestationQuery query,
        RecompositionOptions options,
        CancellationToken ct)
    {
        // For each target token:
        //   1. Query firefly cluster from substrate.physicality
        //   2. Compute consensus position (centroid if tight; archetype if scattered)
        //   3. Expand 4D to target hidden_dim via chosen basis strategy
        //   4. Pack to target dtype
        //   Tokens with no fireflies → zero row (honest abstention)
        //
        // Implementation uses native substrate.st_4d_centroid for aggregation,
        // optionally substrate.st_4d_hausdorff_distance for shape-archetype matching.
    }
}
```

Native compute primitives needed:
- `substrate.st_4d_centroid` (already exists per `sql/schema/functions/`)
- `substrate.st_4d_hausdorff_distance` (already exists)
- `substrate.st_4d_frechet_distance` (already exists)
- Forward 4D → H expansion: trivial in C# or via Eigen vectorized ops
- Optional Hadamard / DCT basis: `Compute.Common.OrthonormalBasis` (new, ~50 lines)

**No InverseLaplacianEigenmap primitive needed.** Removed from the Phase C compute facade scope.

---

## Honest abstention semantics

- Token with **no fireflies** (no ingested model has an embedding for it) → exact zero embedding row in output. Coverage statistic reports this.
- Token with **fragmented firefly cluster** (Hausdorff diameter > threshold OR Voronoi cell tightness below threshold) → flagged as low-consensus; can choose Mode 2 shape-archetype OR honest-abstain to a "default" position.
- Token with **single firefly** (only one ingested model attested it) → that single firefly IS the consensus; sigma is wide; report as low-confidence in metadata.
- Token with **tight cluster** (typical case for common tokens once N ≥ 5 ingested models) → centroid is reliable; Mode 1 centroid consensus.

Per-tensor coverage statistics emitted to safetensors header:
- % tokens with non-zero embedding rows
- % tokens with tight clusters (Mode 1) vs scattered (Mode 2) vs single (low-confidence) vs none (zero)
- Mean firefly cluster radius across the vocabulary

---

## Empirical question: cluster tightness across N models

OPEN — to be measured once Phase A ingestion runs against the model farm. Hypothesis: for common content tokens (function words, frequent nouns, well-known entities), N ≥ 10 ingested models will produce tight Mode-1 clusters. For rare or domain-specific tokens, clusters may scatter across S³ requiring Mode 2 shape archetypes. Empirical measurement determines per-token mode selection in production. Cluster geometry IS itself a substrate analytics surface (per spec §X) regardless of which mode synthesis uses.

**Coordinate-frame caveat (added 2026-05-09).** Each ingested model's `EmbeddingFireflyPass` (Phase A.1: `EmbeddingLayerDecomposer`) computes its Laplacian eigenmap on THAT MODEL's own k-NN graph, producing fireflies in a per-model basis (not a substrate-wide shared frame). Naive centroid aggregation across models therefore averages coordinates in mismatched bases — geometrically meaningless without alignment. The substrate's content-addressed entity identity provides the alignment anchor: every shared word_form across models can serve as an alignment anchor token. Two viable approaches:

- **Anchor-token Procrustes alignment at decomposition time** (~100 lines added to `EmbeddingLayerDecomposer`): pick top-K most-frequent shared word_forms across all ingested tokenizers (queryable via `has_token_in_tokenizer` edges); compute Kabsch rotation matrix that maps this model's anchor-firefly positions onto the substrate's canonical anchor positions; apply rotation to all of this model's fireflies before storage. Result: post-alignment fireflies are approximately in a shared frame; Mode 1 centroid synthesis becomes viable.

- **No alignment; Mode 2 shape consensus only**: skip the alignment step; rely on Hausdorff/Fréchet for per-entity cluster shape comparison (works without coordinate-frame matching because shape distances are rotation-aware in the substrate's 4D operators). Cluster shape comparison is naturally per-entity (one cluster per word_form) so the entity hash IS the alignment scope.

**Recommendation:** implement anchor-Procrustes alignment in `EmbeddingLayerDecomposer` (it's cheap; ~one Kabsch SVD per model ingest = sub-millisecond), unblocking both Mode 1 and Mode 2 synthesis paths. The decision can be revisited empirically after Phase A ingestion produces real cluster geometry data.

## Phased ingestion of the model farm

Safetensors packages are modular at the tensor level — each tensor is independently addressable via the safetensors header's offset table. This enables phased ingestion of the model farm: instead of decomposing each model end-to-end one at a time, decompose ALL models for ONE layer-type at a time. Concrete phases:

1. **Phase Ingest-1 (metadata):** all models' `config.json` + `tokenizer.json` + `tokenizer_config.json` + `model_index.json` + README. Cheap; builds the metadata + tokenizer layer rapidly. Word_form entities get created via `HuggingFaceTokenizerDecomposer`. Substrate gets the model architecture + tokenizer surface in hours, not days.

2. **Phase Ingest-2 (embeddings):** all models' embedding tensors run through `EmbeddingLayerDecomposer`. Firefly POINTZMs populate per-(model, token); attestation edges between word_forms via `model_input_embedding`. After this phase, ALL clusters per word_form are formed; firefly tightness analytics become queryable.

3. **Phase Ingest-3 (attention):** all models' Q/K/V/O tensors run through `AttentionQkvLayerDecomposer` + `AttentionVoLayerDecomposer`. Cross-model consensus on attention patterns tightens.

4. **Phase Ingest-4 (FFN + LM head):** all models' FFN + LM head tensors run through their decomposers. Cross-model consensus on FFN-as-KV-memory patterns + lm-head projections tightens.

5. **Phase Ingest-5 (specialist):** Conv, ViT-patch, CodecRVQ, CrossAttention, etc. — only for the models in the farm that have these layer types.

Implementation: `SafetensorsContainerDecomposer.BuildPassSet` already dispatches per pass; add a `--phase <name>` CLI option that filters the dispatch to only the relevant subset. Each phase processes the entire model farm before moving on. Per-phase substrate state becomes immediately analytics-queryable; iteration on a single layer-type's decomposer (e.g., refining the attention top-K threshold) only requires re-running that phase, not full re-ingestion of every model.

Phased ingestion ALSO handles the case where the model farm is too large to ingest in one pass — break it across days, restart per phase, parallelize across machines per phase.

---

## Cross-references

- [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §VII (firefly model)
- [`docs/specs/engine/embedding-physicality.md`](../../engine/embedding-physicality.md) (Borsuk-Ulam d=4 minimum, forward Laplacian eigenmap)
- [`lottery-ticket-foundations.md`](lottery-ticket-foundations.md) (LTH compound effect on cluster tightening)
- [`synthesis-hardware-integration.md`](../../native/synthesis-hardware-integration.md) (Eigen / oneMKL primitives for the centroid + expansion math)
- `src/Hartonomous.Core/Compute/Ingestion/LaplacianEigenmap.cs` (forward decomposition; used by `EmbeddingLayerDecomposer`)
- `sql/schema/functions/dist_4d.sql` and friends (substrate 4D operators)

## References

- Belkin, M., & Niyogi, P. (2003). *Laplacian Eigenmaps for Dimensionality Reduction and Data Representation*. JMLR 14, 1373–1396.
- Bengio, Y., et al. (2003). *Out-of-Sample Extensions for LLE, Isomap, MDS, Eigenmaps, and Spectral Clustering*. arXiv cs/0303020. (Forward formula for out-of-sample; the inverse direction is what fireflies-as-embedding sidesteps.)
- Borsuk, K. (1933). On the d=4 minimum dimension theorem.
- Frankle, J., & Carbin, M. (2018). *The Lottery Ticket Hypothesis*. arXiv:1803.03635. (Foundational; see lottery-ticket-foundations.md for the connection to consensus tightening.)
