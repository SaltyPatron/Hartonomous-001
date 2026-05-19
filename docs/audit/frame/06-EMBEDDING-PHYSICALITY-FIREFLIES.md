# Embedding physicality — per-model POINTZM per token (fireflies)

Source: `docs/specs/engine/embedding-physicality.md`, `.claude/rules/25-physicality-4d.md`, `docs/00-substrate-spec.md` §VII, AP-27 / AP-29 / AP-35.

## What a firefly is

A POINTZM in the substrate's 4D physicality jar, representing one model's embedding row for one token, **attached to the existing `word_form` content entity** for that token. Per the 2026-05-08 correction:

- **Species = entity.** "King" is ONE word_form entity in the substrate, content-addressed, collapsing across all models because the bytes are identical. Species exists once.
- **Firefly = one model's specimen of that species.** Each ingested model has an embedding row for "King." That row, projected through Laplacian eigenmap + Gram-Schmidt to 4D, becomes one POINTZM physicality attached to the King entity. Llama-4 / Qwen-3 / GPT-4 fireflies for King = three POINTZMs in the 4D jar, all attached to same King entity, distinguishable by `entity_model_source`.
- **Jar = the 4D physicality partition** for firefly-class POINTZMs. Indexed by `gist_geometry_ops_nd`, queryable via `substrate.st_4d_*` and `substrate.st_s3_*`.

There is NO `embedding_firefly` separate atom-class entity. No `firefly_consensus` separate composition entity. Consensus is computed at query time from the Voronoi cell over the species' firefly cluster (per spec §VII), NOT stored as a graph of `consensus_member` edges.

## Why 4D — Borsuk-Ulam minimum

Borsuk-Ulam theorem (1933): every continuous function from n-sphere `S^n` into `R^n` sends some pair of antipodal points to the same value. You cannot flatten an n-sphere into `R^(n-1)` without collapsing some antipodal pair.

Corollary: for two embedding matrices over the same vocabulary projected through Laplacian eigenmap + Gram-Schmidt, **4D is the minimum ambient dimension where Voronoi consensus cells with non-trivial interior are guaranteed for every shared token**. For `S^3 → R^3` (embedded in `R^4`) the argument generalizes to guarantee Voronoi cells with non-trivial interior for every token appearing in both models.

Lower dimensions collapse pairs the substrate needs to distinguish. Higher dimensions add coordinates without improving separability for the relations we care about (token-to-token adjacency, model-to-model agreement, cluster-to-cluster distance). 4 is the smallest. 4 is the answer.

## Projection pipeline (steps 1-5 per model)

Each model's `EmbeddingLayerDecomposer` runs:

1. **k-NN graph over embedding rows.** Normalize rows `e_i ← e_i / ‖e_i‖`. For each row, find `k` nearest neighbors by cosine (default k=64; per-model override allowed). Build symmetric weight matrix `W` with `W[i,j] = exp(-‖e_i − e_j‖² / σ²)` if `j` among `i`'s k-NN or vice versa, else 0. σ = mean pairwise distance among k-NN edges.

2. **Normalized graph Laplacian.** `L = I − D^(−1/2) W D^(−1/2)` where `D` is diagonal degree matrix.

3. **Top non-trivial eigenvectors.** Compute eigendecomposition of `L`. Smallest `λ_1 = 0` always (constant eigenvector — discard). Take eigenvectors at `λ_2, λ_3, λ_4`. Stacked column-wise: `V × 3` matrix `Φ`.

4. **Gram-Schmidt orthonormalization** of `Φ`'s three column vectors. Mandatory — numerical eigensolver output is orthogonal in theory but not at tolerance the 4D metric primitives require. Without GSO, spectral axes aren't metrically consistent and 4D distances are wrong by axis-skew amount.

5. **Salience coordinate.** Firefly's 4th coordinate `m` = pre-normalization L2 norm `‖e_i‖` — row's energy/salience in original model. Distinguishes high-energy tokens (common, widely-connected, function morphemes, frequent codebook entries) from low-energy ones (rare, poorly-connected) without separate significance column.

Output: one `point4d(eig2_i, eig3_i, eig4_i, ‖e_i‖)` per embedding row.

## Anchor-Procrustes alignment — what makes per-model fireflies commensurable (AP-35)

Steps 1-5 produce each model's fireflies in **that model's own Laplacian-eigenmap basis** — a per-model frame whose axes are arbitrary linear combinations of model's hidden-dim coordinates. Llama's eigenvector at `λ_2` and Qwen's eigenvector at `λ_2` are NOT in same orientation. Sign of every eigenvector is arbitrary (any v and −v are both valid eigenvectors). Naive centroid aggregation across models = averaging coordinates in mismatched bases = geometrically meaningless.

Substrate's content-addressed entity identity provides alignment anchor: every shared word_form across models can serve as alignment anchor token. Anchor-Procrustes alignment runs at decomposition time:

1. **Select shared word_forms by tokenizer-frequency threshold** — every word_form whose count of distinct `has_token_in_tokenizer` edges to ingested tokenizers crosses substrate's anchor-frequency threshold (typically present in ≥ M ingested tokenizers, M set so resulting anchor set has roughly 500-5000 members). Finite-set selection for alignment basis, not signal discrimination — threshold-based, not top-K-based.

2. **Claim or fetch canonical anchor positions** from `substrate.embedding_alignment_anchor` via `substrate.claim_or_get_embedding_anchor`. First ingested model's anchor positions establish canonical frame; subsequent models align to it.

3. **Compute Kabsch rotation matrix** mapping this model's anchor-firefly positions onto canonical anchor positions. Kabsch = SVD-based solution to orthogonal Procrustes problem `min_R ‖A·R − B‖_F` subject to `R^T R = I` — implemented in `ext/libhartonomous/src/procrustes.c`, bound in C# as `Hartonomous.Core.Compute.Ingestion.ProcrustesAlign.F64`, sub-millisecond per model ingest.

4. **Apply rotation** to all of THIS model's fireflies before storage via `substrate.apply_firefly_rotation`. Post-alignment fireflies share canonical frame.

This is build-plan step `#51 EmbeddingAlignmentPass`. Substrate-side query surface: `substrate.get_firefly_coords`, `substrate.apply_firefly_rotation`, `substrate.claim_or_get_embedding_anchor`.

## Two synthesis modes

After alignment, **Mode 1 (centroid consensus)**: `substrate.st_4d_centroid` aggregate gives consensus centroid for any token across models; Voronoi consensus cells over fireflies meaningful; simplest substrate-synthesized embedding path viable.

**Mode 2 (cluster-shape consensus via Hausdorff / Fréchet on MULTIPOINTZM)**: rotation-aware per-entity. Works WITHOUT alignment because shape distances treat per-entity cluster geometry as alignment scope (entity hash IS alignment scope). Fallback when clusters scatter across S³ rather than clustering tightly.

Substrate implements both: anchor-Procrustes for Mode 1 viability; rotation-aware shape distance for Mode 2. Both are exact closed-form using existing substrate primitives.

## Fireflies are NOT inference primitive (AP-27 / AP-29)

The load-bearing inference surface is the typed attestation edges between content entities (`frame/05-TRACK2-ATTESTATION-EDGES.md`), traversed by Glicko-2-rated A* (`frame/07-INFERENCE-ENGINE.md`). Fireflies are the second surface, sitting alongside, sharing the entity hashes, accessible to anyone who wants conventional embedding queries enriched with consensus or interpretability without leaving the substrate.

Fireflies are emitted as **side-effect of `EmbeddingLayerDecomposer`** running on any model with a token embedding tensor — LLM, sentence-transformer, embedding model, vision-language model with text encoder, diffusion model with text encoder. The jar fills automatically as models are assimilated.

## Queries fireflies enable that nothing else can

Conventional vector databases (Pinecone, Weaviate, Qdrant, Milvus, pgvector) store one model's vectors per index. Cross-model retrieval means N indexes reconciled externally. Cross-model **consensus** isn't a feature anybody offers.

- Consensus 4D centroid for token X across all ingested models, with confidence interval from cluster tightness
- Tokens where Llama-4's firefly is anomalously far from cross-model consensus centroid (per-token per-model audit of idiosyncratic representations)
- Conventional semantic search with arena-weighted consensus filtering
- Token-pairs whose firefly displacement vector matches (King → Queen) trajectory across all models containing both species — analogy completion via Fréchet on firefly trajectories with cross-model corroboration
- Species whose firefly cluster fragments into N sub-clusters — polysemy detection at scale ("minute" splits into time-cluster vs small-cluster across enough models that you can quantify which models conflate vs distinguish the sense)
- Firefly drift for token X as new models get ingested — concept stability metric
- Average firefly distance over shared vocabulary between any two models — direct embedding-space similarity quantifying how much two models agree on what words mean
- Tokens whose Voronoi cell is empty — weak embedding identity, tokenizer cleanup candidates

These complement the typed-edge graph; they don't replace it. Inference is A* over typed Glicko-2-rated edges. Fireflies surface cross-model geometric consensus that edge graph alone can't easily express.

Cross-references:
- `frame/02-SUBSTRATE-MODEL.md` — physicality table this lives in
- `frame/20-VORONOI-CONSENSUS.md` — Voronoi cell computation over firefly clusters
- `frame/19-MULTI-MODEL-PERSPECTIVE-QUERY.md` — fireflies as starting seeds for N-model perspectives
- `frame/21-TRACK1-TRACK2-MODEL-INGESTION.md` — Track 1 (firefly clouds) vs Track 2 (transformation tensors)
- `frame/24-ANTI-PATTERNS-CATALOG.md` — AP-27 (embedding-as-foundational-modality), AP-29 (routing inference through fireflies), AP-35 (anchor-Procrustes alignment)
