# Voronoi Consensus — Cross-Source Convergence over Firefly Clouds

> **Authority note (2026-05-09):** The Voronoi consensus mechanism described in this document is correct in spirit (cross-model agreement on a token's hidden-space identity emerges from the geometry of its firefly cluster). The 2026-05-08 architectural correction changes one specific implementation detail: **consensus is COMPUTED at query time from the Voronoi cell over the species' firefly cluster, NOT stored as a separate `firefly_consensus` composition entity with `consensus_member` / `consensus_supersedes` edges.** Per [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VII and §X.1, fireflies are POINTZM physicalities attached to existing `word_form` content entities (the species); consensus tightness, centroid, and spread metrics are derived analytics surfaces (per spec §X — analytics caches, materialized views, rebuildable from substrate state, NOT substrate truth). Where this document references `firefly_consensus` as a stored entity type, treat as DEPRECATED — read instead as "the consensus computed from the Voronoi cell over the species' firefly POINTZM cluster." See AP-29.

**Status:** Mechanism canonical; storage shape (consensus-as-entity) deprecated per the authority note above.
**Last verified:** 2026-05-09 (post architectural-correction sweep).
**Audience:** Engineers implementing the consensus computation, anyone building cross-model refinement recipes, anyone who wants to understand how the substrate produces "the field's collective answer" from N source models.

---

## The problem

Given N models that have each contributed fireflies (4D projections) to the same conceptual cloud — for example, all decoder-only LLMs' fireflies for "attention head 12 of layer 47" — what is the substrate's consensus position?

The naive answer is "the centroid." That collapses too much: it weights every model equally and treats outliers as noise rather than signal. It also throws away spread information that downstream queries need.

The Voronoi consensus answers the question with three outputs:

1. A **consensus centroid** — a single 4D point that represents the field's converged position, weighted by per-arena authority.
2. A **consensus shape** — a LINESTRING4D over contributing fireflies that preserves their relative ordering and lets downstream geometry queries (Fréchet, Hausdorff) operate on the cloud as a single composition.
3. A **divergence metric set** — per-firefly distance from consensus, and cloud-wide spread metrics (max distance, distribution shape, bimodality flag).

## Why Voronoi specifically

The Voronoi tessellation of a point set partitions space into cells; each cell contains the points closest to one specific "site" (firefly). The cell's volume reflects how isolated that site is from its neighbors — a small cell means the site is in a dense region (multiple models agree); a large cell means the site is an outlier.

Cell volumes give the substrate a principled weighting: a firefly in a dense agreement region contributes proportionally to how many neighbors echo it. A firefly in a sparse region contributes by its standalone authority but does not get amplified by absent neighbors.

This is more honest than naive averaging because it does not let one model's contribution stand in for many — and more honest than majority voting because it preserves the geometric distribution rather than collapsing to a single winner.

## Inputs

- **Cloud**: a set of `firefly` entities sharing a conceptual position (same projection slot across multiple models). Cloud membership is determined by provenance + projection-position match; it is not a stored set but is materialized on demand.
- **Arena**: the arena name that determines per-firefly authority weight. For model fireflies, the arena is typically architecture-specific or task-specific (e.g., `decoder_only_llm_general`, `code_generation`, `medical_qa`). Per-arena Glicko-2 ratings drive weights.
- **Tier**: granularity tier — `weight`, `row`, `col`, `head`, `layer`, or `block`. The tessellation operates on points within a single tier.

## Algorithm

### Step 1 — Materialize the cloud

```sql
SELECT
  f.firefly_id,
  f.centroid_4d,
  f.provenance,
  rating.mu,
  rating.phi,
  rating.sigma
FROM substrate.firefly f
JOIN substrate.firefly_position_match($conceptual_position) USING (firefly_id)
LEFT JOIN substrate.arena_rating(f.firefly_id, $arena) AS rating ON true
WHERE f.tier = $tier
```

The `firefly_position_match` SPI enumerates fireflies whose projection position matches the requested conceptual position. The match is exact: same architecture-handler + same architectural slot. Fireflies from incompatible architectures are excluded by construction.

### Step 2 — Compute Voronoi tessellation

The 4D Voronoi tessellation is computed via the substrate's geometric primitives in `hartonomous_pg`. The implementation uses the standard Bowyer–Watson algorithm extended to 4D:

1. Initialize with a 4-simplex bounding all fireflies (the "super-simplex").
2. Insert each firefly one at a time:
   - Find all simplices whose circumsphere contains the firefly.
   - Remove those simplices, leaving a hole.
   - Re-tessellate the hole with new simplices connecting the firefly to the hole's boundary faces.
3. Remove all simplices that touch a super-simplex vertex.
4. The remaining simplices form the Delaunay triangulation; the Voronoi tessellation is its dual.

Each firefly's Voronoi cell is computed by:
- Finding all Delaunay simplices incident to the firefly.
- Computing each simplex's circumcenter.
- Connecting circumcenters in order around the firefly.
- The resulting polytope is the Voronoi cell.

The cell volume is computed via the Cayley-Menger determinant for each constituent simplex.

The Voronoi computation is O(N log N) for N fireflies in low dimensions and degrades in high dimensions, but 4D is well within practical range. Clouds with N > 10⁶ fireflies (rare; only happens for `weight`-tier clouds across many large models) use a hierarchical approximation: tessellate at `head`-tier first, then refine within each `head`-tier cell.

### Step 3 — Authority-weighted centroid

For each firefly i:
- Let `vol_i` = Voronoi cell volume (clipped to a max to prevent unbounded outliers from dominating).
- Let `auth_i` = max(0, mu_i - 2·phi_i) where mu_i and phi_i are the firefly's Glicko-2 rating in the arena. This is the conservative rating estimate (lower-bound of 95% CI).
- Let `weight_i` = auth_i / (1 + log(1 + vol_i)). The log dampens cell-volume effect; pure-volume weighting would over-amplify isolated points.

Consensus centroid = `Σ(weight_i · centroid_i) / Σ(weight_i)`.

If `Σ(weight_i) = 0` (no fireflies have positive authority — possible for an unrated arena or all-zero ratings), fall back to unweighted centroid and flag the consensus as `unweighted` in metadata.

### Step 4 — Consensus LINESTRING4D

Order fireflies by descending `weight_i`. The consensus composition's `physicality_4d` is `LINESTRING4D` over the ordered firefly centroids. The composition's `centroid_4d` is the authority-weighted centroid from Step 3 (NOT the geometric centroid of the linestring — those differ when weights are non-uniform).

This composition is the substrate's stored consensus artifact. It is a `firefly_consensus` entity addressable by `BLAKE3(arena || conceptual_position || tier || ordered_firefly_ids)`.

### Step 5 — Divergence metrics

For each firefly:
- `distance_from_consensus` = 4D geodesic distance from firefly centroid to consensus centroid.
- `cell_volume_normalized` = cell volume / median cell volume in the cloud.
- `outlier_flag` = true if distance > 2·median(distances) AND cell_volume > 3·median(cell_volume). Outliers are flagged but not removed; downstream queries decide whether to include them.

Cloud-wide:
- `max_distance` = max distance from any firefly to consensus centroid.
- `median_distance` = median distance.
- `bimodality_flag` = computed via Hartigan's dip test on the distance distribution. Bimodal clouds indicate the field has split into two camps; downstream consumers may want to compute consensus per cluster instead of globally.
- `dispersion` = mean distance / max distance, a 0-to-1 measure of how spread the cloud is.

## Substrate state produced

For each consensus computation, the substrate emits or updates:

- One `firefly_consensus` composition entity per (arena, conceptual_position, tier).
- `consensus_member` edges from the composition to each contributing firefly, with edge attributes including the firefly's `weight_i`, `cell_volume_i`, `distance_from_consensus_i`.
- `consensus_supersedes` edges to the previous consensus composition for the same (arena, position, tier), if any. The full history is retained — supersession is an edge, not a deletion. This preserves the substrate's audit trail across consensus revisions.
- An `audit_trace` entity documenting the consensus computation: which fireflies were included, the algorithm parameters, the timestamp.

## Update triggers

Consensus is recomputed:

1. **On firefly ingestion.** When a new model is ingested and contributes fireflies to existing clouds, those clouds' consensus is updated. This is part of the model decomposer's atomic ingestion pass (see `20-technical/04-model-decomposer.md`).
2. **On Glicko-2 rating update.** When outcome events accumulate beyond the per-update threshold and trigger a batched Glicko update for fireflies in an arena, downstream consensus is recomputed for the affected clouds. This is invoked from the macro-OODA loop (see `10-architecture/10-godel-engine.md`).
3. **On explicit recipe request.** Recipes may force re-computation, e.g., when a recipe wants to compute consensus over a specific subset of models or arenas.

Consensus is NEVER recomputed during inference. Substrate Law 9 forbids inference-side substrate writes; consensus updates are ingestion-side or scheduled.

## Performance

| Cloud size | Tessellation time | Update cost |
|---|---|---|
| < 100 fireflies | < 1 ms | Negligible |
| 100–1000 | 1–50 ms | Subsecond |
| 1000–10000 | 50 ms – 5 s | Seconds |
| 10000–100000 | 5 s – 5 min | Background batch |
| > 100000 | Hierarchical approximation; minutes to tens of minutes | Background batch |

Default ingestion populates `head`-tier and `layer`-tier clouds, which typically stay well under 10K fireflies even with hundreds of ingested models.

`weight`-tier consensus is computed lazily on demand only — the cost is too high for eager update on every model ingestion. Recipes that need weight-tier consensus opt in explicitly.

## What consensus does NOT do

- **It does not delete fireflies.** Outliers are flagged, not removed. Every model's contribution remains queryable.
- **It does not collapse architectures.** Fireflies from a decoder-only LLM and a vision encoder do not enter the same cloud unless their projection positions match (which they do not, in general). Cross-architecture comparison is a separate operation, not consensus.
- **It does not hide the source models.** Every consensus composition has `consensus_member` edges to all contributing fireflies, each with provenance back to its source model. Auditing "where did this consensus come from" is a graph traversal.
- **It does not produce a probability distribution.** The consensus is a deterministic geometric construction. Variation across consensus revisions reflects substrate state changes, not stochastic resampling.

## Worked example

Setup: 8 decoder-only LLMs ingested. Cloud of interest: `head`-tier fireflies for "head 8 of layer 24 QK-projection" across all 8 models. Arena: `code_generation`.

Per-model contributions:

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

Tessellation: 8 Voronoi cells in 4D. Cell volumes vary roughly with isolation; DeepSeek-Coder and StarCoder 2 form a tight pair (small cells); Gemini 1.5 is the most isolated (largest cell).

Authority weights (`mu - 2·phi`):

- GPT-4: 1790
- Claude 3.5: 1824
- Gemini 1.5: 1750
- Llama 3 70B: 1620
- Mistral Large: 1674
- Qwen 2.5: 1708
- DeepSeek-Coder: 1850
- StarCoder 2: 1756

Consensus centroid (authority-weighted, log-dampened by cell volume): approximately (0.40, 0.13, 0.79, 0.30) — pulled toward the DeepSeek/StarCoder/Claude region because those have the highest code-generation authority.

`bimodality_flag = false` (the cloud is unimodal; all models cluster within a small region).
`dispersion = 0.25` (relatively tight consensus).

Outcome edges:
- A `firefly_consensus` composition with `physicality_4d` = LINESTRING4D ordered by descending weight: DeepSeek → Claude → GPT-4 → StarCoder → Qwen → Mistral → Gemini → Llama.
- 8 `consensus_member` edges, each with `weight_i`, `cell_volume_i`, `distance_from_consensus_i`.
- An `audit_trace` documenting the computation.

A subsequent ingestion of "Llama 4 Maverick" with a code-generation Glicko of 1860 and firefly at (0.39, 0.11, 0.81, 0.29) would:
- Add a firefly to the cloud.
- Recompute the Voronoi tessellation.
- Shift the consensus centroid slightly toward Maverick (high authority + close to existing dense region).
- Emit a new `firefly_consensus` composition that supersedes the prior one (the prior composition is retained with a `consensus_supersedes` edge).

A query "show me the most divergent decoder-only LLM head, by code-generation arena" would traverse `consensus_member` edges, sort by `distance_from_consensus_i`, and return Gemini 1.5 — its head 8 / layer 24 QK-projection sits furthest from the field's converged position.

## Cross-references

- Track 1 fireflies (the inputs to consensus): `10-architecture/11-track1-track2-model-ingestion.md`
- Glicko-2 in arenas (the authority weighting source): `10-architecture/04-arenas.md`
- Macro-OODA (where scheduled consensus updates live): `10-architecture/10-godel-engine.md`
- 4D geometric primitives (the tessellation implementation): `10-architecture/03-geometry.md`
- Substrate Law 9 (consensus updates are ingestion-side, not inference-side): `10-architecture/01-substrate-laws.md`
- Three-level idiomaticity (Voronoi consensus is the cloud-level metric in this hierarchy): `10-architecture/14-idiomaticity.md` (forthcoming)

## External references

- Voronoi diagram: <https://en.wikipedia.org/wiki/Voronoi_diagram>
- Bowyer–Watson algorithm: <https://en.wikipedia.org/wiki/Bowyer%E2%80%93Watson_algorithm>
- Hartigan's dip test: <https://projecteuclid.org/journals/annals-of-statistics/volume-13/issue-1/The-Dip-Test-of-Unimodality/10.1214/aos/1176346577.full>
- Glicko-2 rating system (Mark Glickman): <http://www.glicko.net/glicko/glicko2.pdf>
