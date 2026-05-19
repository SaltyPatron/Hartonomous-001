# Voronoi consensus — cross-source convergence over firefly clouds

Source: `docs/10-architecture/12-voronoi-consensus.md`.

> **Authority note (2026-05-09)**: The Voronoi consensus mechanism remains canonical (cross-model agreement on a token's hidden-space identity emerges from geometry of its firefly cluster). The 2026-05-08 correction changes implementation: **consensus is COMPUTED at query time from Voronoi cell over species' firefly cluster, NOT stored as separate `firefly_consensus` composition entity with `consensus_member` / `consensus_supersedes` edges.** Consensus tightness, centroid, spread metrics are derived analytics surfaces (per spec §X — analytics caches, materialized views, rebuildable from substrate state, NOT substrate truth). Where this doc references `firefly_consensus` as stored entity type, treat as DEPRECATED — read instead as "consensus computed from Voronoi cell over species' firefly POINTZM cluster."

## The problem

Given N models that have each contributed fireflies to the same conceptual cloud (e.g., all decoder-only LLMs' fireflies for "attention head 12 of layer 47") — what is the substrate's consensus position?

Naive answer "the centroid" collapses too much: weights every model equally, treats outliers as noise rather than signal, throws away spread information downstream queries need.

Voronoi consensus answers with three outputs:
1. **Consensus centroid** — single 4D point representing field's converged position, weighted by per-arena authority
2. **Consensus shape** — LINESTRING4D over contributing fireflies preserving relative ordering, lets downstream geometry queries (Fréchet, Hausdorff) operate on cloud as single composition
3. **Divergence metric set** — per-firefly distance from consensus + cloud-wide spread metrics (max distance, distribution shape, bimodality flag)

## Why Voronoi specifically

Voronoi tessellation of a point set partitions space into cells; each cell contains points closest to one specific site (firefly). Cell volume reflects how isolated that site is from neighbors — small cell = dense region (multiple models agree); large cell = outlier.

Cell volumes give substrate principled weighting: firefly in dense agreement region contributes proportionally to how many neighbors echo it. Firefly in sparse region contributes by standalone authority but does not get amplified by absent neighbors.

More honest than naive averaging (doesn't let one model's contribution stand in for many) and more honest than majority voting (preserves geometric distribution rather than collapsing to single winner).

## Inputs

- **Cloud**: set of `firefly` entities sharing conceptual position (same projection slot across multiple models). Cloud membership determined by provenance + projection-position match; materialized on demand, not stored as a set.
- **Arena**: arena name that determines per-firefly authority weight. For model fireflies, typically architecture-specific or task-specific (`decoder_only_llm_general`, `code_generation`, `medical_qa`). Per-arena Glicko-2 ratings drive weights.
- **Tier**: granularity tier — `weight`, `row`, `col`, `head`, `layer`, `block`. Tessellation operates on points within single tier.

## Algorithm

### Step 1 — Materialize cloud
```sql
SELECT f.firefly_id, f.centroid_4d, f.provenance, rating.mu, rating.phi, rating.sigma
FROM substrate.firefly f
JOIN substrate.firefly_position_match($conceptual_position) USING (firefly_id)
LEFT JOIN substrate.arena_rating(f.firefly_id, $arena) AS rating ON true
WHERE f.tier = $tier
```

`firefly_position_match` SPI enumerates fireflies whose projection position matches requested conceptual position. Exact match: same architecture-handler + same architectural slot. Fireflies from incompatible architectures excluded by construction.

### Step 2 — Compute 4D Voronoi tessellation (Bowyer-Watson extended to 4D)

1. Initialize with 4-simplex bounding all fireflies (super-simplex)
2. Insert each firefly one at a time:
   - Find all simplices whose circumsphere contains the firefly
   - Remove those simplices, leaving a hole
   - Re-tessellate the hole with new simplices connecting the firefly to hole's boundary faces
3. Remove all simplices that touch a super-simplex vertex
4. Remaining simplices form Delaunay triangulation; Voronoi tessellation is its dual

Each firefly's Voronoi cell computed by:
- Finding all Delaunay simplices incident to firefly
- Computing each simplex's circumcenter
- Connecting circumcenters in order around firefly
- Resulting polytope is the Voronoi cell

Cell volume via Cayley-Menger determinant for each constituent simplex.

Voronoi computation is O(N log N) for low dimensions; 4D well within practical range. Clouds with N > 10⁶ fireflies (rare; only `weight`-tier across many large models) use hierarchical approximation: tessellate at `head`-tier first, then refine within each `head`-tier cell.

### Step 3 — Authority-weighted centroid

For each firefly i:
- Let `vol_i` = Voronoi cell volume (clipped to max to prevent unbounded outliers from dominating)
- Let `auth_i` = max(0, mu_i - 2·phi_i) — conservative rating estimate (Glicko-2 95% lower-bound CI)
- Let `weight_i` = auth_i / (1 + log(1 + vol_i)) — log dampens cell-volume effect (pure-volume weighting would over-amplify isolated points)

Consensus centroid = `Σ(weight_i · centroid_i) / Σ(weight_i)`.

If `Σ(weight_i) = 0` (no fireflies have positive authority — possible for unrated arena or all-zero ratings), fall back to unweighted centroid and flag as `unweighted` in metadata.

### Step 4 — Consensus LINESTRING4D

Order fireflies by descending `weight_i`. Consensus composition's `physicality_4d` = LINESTRING4D over ordered firefly centroids. Composition's `centroid_4d` = authority-weighted centroid from Step 3 (NOT geometric centroid of linestring — those differ when weights non-uniform).

Composition addressable by `BLAKE3(arena || conceptual_position || tier || ordered_firefly_ids)`.

### Step 5 — Divergence metrics

Per-firefly:
- `distance_from_consensus` = 4D geodesic distance from firefly centroid to consensus centroid
- `cell_volume_normalized` = cell volume / median cell volume in cloud
- `outlier_flag` = true if distance > 2·median(distances) AND cell_volume > 3·median(cell_volume). Outliers flagged but not removed; downstream queries decide whether to include them.

Cloud-wide:
- `max_distance` = max distance from any firefly to consensus centroid
- `median_distance` = median distance
- `bimodality_flag` = computed via Hartigan's dip test on distance distribution. Bimodal clouds indicate field has split into camps; downstream consumers may want to compute consensus per cluster instead of globally.
- `dispersion` = mean distance / max distance — 0-to-1 measure of how spread cloud is

## Substrate state produced (legacy shape — deprecated per authority note)

Per the authority note, consensus is COMPUTED at query time as analytics cache (per spec §X.1), NOT stored as separate entity. The legacy shape (`firefly_consensus` composition + `consensus_member` edges + `consensus_supersedes` edges + `audit_trace`) remains a useful conceptual model of what the analytics cache contains, but no longer reflects implementation.

## Update triggers (legacy shape — under new analytics-cache model, "update" = refresh of cache)

1. **On firefly ingestion** — when new model ingested and contributes fireflies to existing clouds, those clouds' consensus is recomputed
2. **On Glicko-2 rating update** — when outcome events accumulate beyond per-update threshold and trigger batched Glicko update for fireflies in an arena, downstream consensus recomputed for affected clouds (macro-OODA)
3. **On explicit recipe request** — recipes may force recomputation (compute consensus over specific subset of models or arenas)

Consensus is NEVER recomputed during inference. Substrate Law 9 forbids inference-side substrate writes; consensus updates are ingestion-side or scheduled.

## Performance

| Cloud size | Tessellation time | Update cost |
|---|---|---|
| < 100 fireflies | < 1 ms | Negligible |
| 100-1000 | 1-50 ms | Subsecond |
| 1000-10000 | 50 ms - 5 s | Seconds |
| 10000-100000 | 5 s - 5 min | Background batch |
| > 100000 | Hierarchical approximation; minutes to tens of minutes | Background batch |

Default ingestion populates `head`-tier and `layer`-tier clouds, which stay well under 10K fireflies even with hundreds of ingested models. `weight`-tier consensus computed lazily on demand only — cost too high for eager update on every model ingestion. Recipes that need weight-tier consensus opt in explicitly.

## What consensus does NOT do

- **Does NOT delete fireflies** — outliers flagged, not removed. Every model's contribution remains queryable.
- **Does NOT collapse architectures** — fireflies from decoder-only LLM and vision encoder do not enter same cloud unless projection positions match (which they do not, in general). Cross-architecture comparison is separate operation, not consensus.
- **Does NOT hide source models** — every consensus composition has `consensus_member` edges to all contributing fireflies, each with provenance back to source model. Auditing "where did this consensus come from" is graph traversal.
- **Does NOT produce probability distribution** — consensus is deterministic geometric construction. Variation across consensus revisions reflects substrate state changes, not stochastic resampling.

## Worked example — 8 decoder-only LLMs for code_generation arena

Cloud: `head`-tier fireflies for "head 8 of layer 24 QK-projection" across all 8 models.

| Model | Firefly position | Glicko mu (code_generation) | Glicko phi |
|---|---|---|---|
| GPT-4 | (0.42, 0.15, 0.78, 0.33) | 1850 | 30 |
| Claude 3.5 | (0.41, 0.14, 0.77, 0.31) | 1880 | 28 |
| Gemini 1.5 | (0.45, 0.18, 0.74, 0.36) | 1820 | 35 |
| Llama 3 70B | (0.39, 0.12, 0.80, 0.29) | 1700 | 40 |
| Mistral Large | (0.43, 0.16, 0.77, 0.34) | 1750 | 38 |
| Qwen 2.5 | (0.40, 0.13, 0.79, 0.30) | 1780 | 36 |
| DeepSeek-Coder | (0.38, 0.10, 0.83, 0.27) | 1900 | 25 |
| StarCoder 2 | (0.37, 0.09, 0.84, 0.26) | 1820 | 32 |

Tessellation: 8 Voronoi cells in 4D. DeepSeek-Coder and StarCoder 2 form tight pair (small cells); Gemini 1.5 most isolated (largest cell).

Authority weights (`mu - 2·phi`): GPT-4 1790, Claude 3.5 1824, Gemini 1.5 1750, Llama 3 70B 1620, Mistral Large 1674, Qwen 2.5 1708, DeepSeek-Coder 1850, StarCoder 2 1756.

Consensus centroid (authority-weighted, log-dampened by cell volume): approximately (0.40, 0.13, 0.79, 0.30) — pulled toward DeepSeek/StarCoder/Claude region (highest code-generation authority).

`bimodality_flag = false` (cloud unimodal; all models cluster within small region). `dispersion = 0.25` (relatively tight consensus).

Subsequent ingestion of "Llama 4 Maverick" (code-gen Glicko 1860, firefly at (0.39, 0.11, 0.81, 0.29)):
- Adds firefly to cloud
- Recompute Voronoi tessellation
- Shift consensus centroid slightly toward Maverick (high authority + close to existing dense region)
- New consensus supersedes prior (prior retained with `consensus_supersedes` edge in legacy shape, or just refreshed in analytics-cache shape)

Query "show me most divergent decoder-only LLM head by code-generation arena" → traverse `consensus_member` edges, sort by `distance_from_consensus_i`, return Gemini 1.5 (its head 8/layer 24 QK-projection sits furthest from field's converged position).

Cross-references:
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — fireflies are inputs to consensus
- `frame/02-SUBSTRATE-MODEL.md` — 4D geometric primitives the tessellation uses
- `frame/11-CONTINUOUS-LEARNING-LOOP.md` — Glicko-2 in arenas (authority weighting source)
- `frame/08-GODEL-ENGINE.md` — macro-OODA where scheduled consensus updates live
- `frame/17-THREE-LEVEL-IDIOMATICITY.md` — cloud-level metric in this hierarchy is Hausdorff
- `frame/18-FRAYED-EDGE-DETECTION.md` — empty consensus cells = frayed edges (geometric gaps to fill)
