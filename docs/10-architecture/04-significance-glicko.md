# Significance — Glicko-2 in Open-Vocabulary Arenas

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers implementing the significance layer, decomposers that emit edges with provenance, inference engineers, and operators tuning arena dynamics.

---

## Why Glicko-2 specifically

The substrate needs an edge-rating system with three properties:

1. **Convergence under repeated observation.** As more sources attest the same edge in the same arena, the rating's uncertainty should decrease. Conventional arithmetic averaging doesn't model uncertainty separately from value.
2. **Comparison-event semantics.** When inference produces an outcome (this path was selected, that path was rejected), the system needs to update both winners and losers' ratings consistently.
3. **Volatility tracking.** A rating that has been stable for thousands of observations should be harder to move than a rating that has been bouncing. Glicko-2 explicitly models this via the volatility parameter σ_volatility (often written as just σ, distinct from σ — the rating uncertainty).

Glicko-2 (Glickman, 2013) provides all three. It's the standard rating system for chess-like contests, board games, and competitive scenarios. The substrate uses it because edges in arenas ARE in a competition: edges win when chosen by inference outcomes, lose when rejected.

Reference: <https://glicko.net/glicko/glicko2.pdf>

## The state per (arena, edge) row

```
substrate.edge_significance (
    context_type_id  INT,        -- arena
    edge_type_id     INT,
    edge_hash        bytea,
    mu               FLOAT8 DEFAULT 1500.0,
    sigma            FLOAT8 DEFAULT 350.0,
    volatility       FLOAT8 DEFAULT 0.06,
    games            INT DEFAULT 0,
    PRIMARY KEY (context_type_id, edge_type_id, edge_hash)
) PARTITION BY LIST (context_type_id);
```

| Column | Meaning | Initial |
|---|---|---|
| `mu` | Rating mean (center of estimated skill) | From `provenance.initial_mu` for the edge's source, or 1500.0 default |
| `sigma` | Rating uncertainty (1-sigma bound on confidence in mu) | 350.0 (high uncertainty until observations accumulate) |
| `volatility` | Meta-uncertainty (how much sigma is expected to change over time) | 0.06 (typical Glicko-2 default) |
| `games` | Number of comparison events processed | 0 |

The same shape exists for `entity_significance` (rates intrinsic content significance) and for the Glicko-bearing junction tables: `entity_pos`, `entity_sense`, `pattern_deprel`. Each junction's significance has the same four columns and updates the same way.

## The Glicko-2 update equations

For a given player (in our case, an edge or entity in a specific arena) facing a series of comparison events in a rating period:

**Step 0: Convert to internal scale.**
- `μ_internal = (mu - 1500) / 173.7178`
- `φ_internal = sigma / 173.7178`
- For each opponent: `μ_opp_internal = (mu_opp - 1500) / 173.7178`, `φ_opp_internal = sigma_opp / 173.7178`

**Step 1: Compute estimated variance v.**
- `g(φ) = 1 / sqrt(1 + 3φ² / π²)`
- `E(μ, μ_opp, φ_opp) = 1 / (1 + exp(-g(φ_opp) * (μ - μ_opp)))`
- `v = 1 / Σ(g(φ_opp_j)² * E_j * (1 - E_j))`  for j over each opponent in the rating period

**Step 2: Compute estimated improvement Δ.**
- `Δ = v * Σ(g(φ_opp_j) * (s_j - E_j))`  where `s_j` is 1 for win, 0 for loss, 0.5 for draw

**Step 3: Compute new volatility σ' (this is the iterative step).**

Solve for `A` such that `f(A) = 0`, where:
- `f(x) = (e^x * (Δ² - φ² - v - e^x)) / (2 * (φ² + v + e^x)²) - (x - a) / τ²`
- `a = ln(σ²)`
- `τ` is the system constant (typically 0.3 to 1.2)

Use the Illinois algorithm (or any 1D root-finder with sign-bracketing) starting from `A = a` and iterating until `|A_new - A| < ε`.

After convergence: `σ_new = exp(A_final / 2)`.

**Step 4: Update φ to pre-rating-period value, then to new value.**
- `φ_pre = sqrt(φ² + σ_new²)`
- `φ_new = 1 / sqrt(1/φ_pre² + 1/v)`

**Step 5: Update μ.**
- `μ_new = μ + φ_new² * Σ(g(φ_opp_j) * (s_j - E_j))`

**Step 6: Convert back to display scale.**
- `mu_display = μ_new * 173.7178 + 1500`
- `sigma_display = φ_new * 173.7178`

For an inactive period (the rating period had no comparisons for this player):
- `φ_new = sqrt(φ² + σ²)`
- `μ` and `σ` unchanged

The native extension implements this update as `glicko2_update(rating, opponent_ratings, outcomes, tau)`. Tests verify against the worked example in Glickman's paper.

## What "rating period" means in the substrate

In the original Glicko-2 specification, a rating period is a chunk of time during which all comparisons for a player are batched and processed together at the end. For the substrate:

- A rating period is the batch of outcome events processed in a single `apply_glicko_updates` call.
- The substrate runs Glicko updates either (a) on demand at inference completion (one update per outcome event), or (b) batched in a periodic background job.
- Approach (a) gives instant feedback; (b) is more efficient at scale.

For (a) — per-outcome updates — the rating period contains exactly one comparison event per call. This is mathematically valid but slightly suboptimal for variance estimation; the system constant τ should be tuned slightly higher to compensate.

For (b) — batched periodic updates — the rating period accumulates comparisons over a window (e.g., 1 hour, 1 day) and processes them together. Better variance estimation, slightly delayed feedback.

The substrate implementation supports both modes. Initial deployment uses per-outcome updates; switching to batched is a configuration change.

## Trust priors at insertion

When a decomposer emits an edge for the first time, the substrate's pipeline either:

**Option A (eager priming):** Insert the edge into `substrate.edge`, then for every existing arena, insert a row into `substrate.edge_significance` with `mu = provenance.initial_mu` for the edge's source, `sigma = 350.0`, `volatility = 0.06`, `games = 0`.

**Option B (lazy materialization):** Insert the edge into `substrate.edge`. Do NOT insert any `edge_significance` rows. Queries that need significance use `COALESCE(s.mu, p.initial_mu)` joining to the edge's `provenance.initial_mu` as the implicit default. Rows are inserted lazily on first outcome event that updates a specific (arena, edge) pair.

**Option B is the recommended approach.** With open-vocabulary arenas (10+ initially, potentially 100+ over time) × billions of edges = potentially 100B significance rows under eager priming, most never accessed. Lazy materialization keeps storage proportional to actually-used rows.

The substrate's API exposes:
- `edge_significance_or_default(arena_id, edge_type, edge_hash)` returns either the stored row or a virtual default row. Hot path uses this.
- `materialize_edge_significance(arena_id, edge_type, edge_hash)` inserts a row at current value. Called by the update path before the Glicko update.

## Provenance trust priors

Initial μ values for canonical sources, calibrated against authority:

| Source | initial_mu | curator_class |
|---|---|---|
| `unicode_consortium` | 2000 | authoritative_standard |
| `sil_international` (ISO 639) | 2000 | authoritative_standard |
| `princeton_wordnet` | 1800 | academic_curated |
| `omwn_consortium` | 1600 | academic_consortium |
| `universaldependencies` | 1600 | academic_consortium |
| `wiktextract` (Wiktionary) | 1400 | community_curated |
| `tatoeba` | 1200 | community_contributed |
| `huggingface_model` | 1500 (varies per model) | model_derived |
| `system_computed` | 1300 | system_computed |
| `user_session` | 1000 | user_input |

Per-model trust priors for `huggingface_model` provenance are sub-classed by model:
- `huggingface_model:llama4-maverick` (frontier, high trust): 1700
- `huggingface_model:qwen3-coder-480b` (frontier coder): 1700
- `huggingface_model:deepseek-v3.2-speciale` (frontier reasoner): 1700
- `huggingface_model:qwen2.5-coder-7b` (mid-tier): 1500
- `huggingface_model:qwen2.5-coder-3b` (smaller): 1450
- ... etc.

Trust priors can be updated as the substrate operator's judgment evolves. Updating a trust prior is a substrate operation, not a migration; existing edges with that provenance keep their current significance, but new attestations from that provenance use the new prior.

## Open-vocabulary arenas

The starter set of arenas (from migration `0005_reference_seed`):

```
1.  lexical_disambiguation       — Which sense fits the context
2.  syntactic_role_fitness       — Which dependency role fits
3.  translation_quality          — Cross-lingual alignment quality
4.  model_trust                  — Confidence in a model's attestations
5.  source_authority             — Source reliability
6.  semantic_relevance           — Topic fit
7.  corroboration_strength       — Cross-source agreement
8.  frequency_significance       — Attestation density
9.  attention_pattern_confidence — Model attention pattern reliability
10. morphological_productivity   — Inflectional patterns
```

New arenas can be added at runtime. Examples of arenas that might be added:

- `pragmatic_register` — formal vs. informal usage
- `temporal_validity` — how stale is this evidence
- `code_safety` — trustworthiness of code patterns
- `medical_consensus` — agreement among medical sources
- `legal_jurisdiction:US` — significance specifically in US legal context
- `english_to_mandarin_translation` — narrower than `translation_quality`
- `qwen3_vs_llama4_attention` — model-pair-specific competition

The substrate function `add_arena(code, description)`:
1. Inserts a row into `ref.significance_context`.
2. Optionally backfills lazy materialization for selected edges (e.g., recently-touched edges, or all edges of specific types).

Code that hardcodes the initial 10 arena codes is wrong. Substrate functions must cross-product against whatever arenas exist at execution time.

## How outcome events drive Glicko updates

Inference produces a path; the path has a list of edges traversed. When the inference outcome is observed (user accept, downstream success, measurable utility), the substrate creates comparison events:

- For each edge `e_winner` in the selected path: compare against edges that COULD HAVE been chosen at the same A* hop but weren't. Each `e_loser` is an opponent of `e_winner` for this rating period.
- The outcome `s` is 1 (win) for `e_winner`, 0 (loss) for `e_loser`. Draws are rare in the substrate context.
- Glicko updates are applied to each (arena, edge) pair in the relevant arenas.

The arenas for the update are:
- Always: `corroboration_strength` (the edge participated in a successful path, increasing confidence in its evidence)
- The arenas the inference query specified (e.g., `lexical_disambiguation`, `semantic_relevance`)
- Provenance-implied arenas (e.g., `model_trust:llama4-maverick` if Llama-4-Maverick attested edges in the path)

User-rejection or task-failure outcomes reverse the win/loss assignments. Path winners become losers and vice versa.

This is closed-loop learning. No gradient descent. No labeled data. Outcomes drive ratings; ratings drive future inference.

## What significance is NOT

- **Not a ranking.** Mu doesn't reflect "edge X is more important than edge Y" globally. It reflects "edge X is more reliable in arena A than the typical edge in A is."
- **Not absolute.** The 1500 baseline is arbitrary; the system is invariant to additive shifts. Rankings WITHIN an arena are meaningful; rankings ACROSS arenas are not.
- **Not a probability.** Mu is a rating in a competitive system; converting it to a probability requires `g(φ) * E(μ, μ_opp, φ_opp)` against a specific opponent. There's no "P(this edge is true) = mu / 3000."
- **Not transitive.** If `mu(A) > mu(B)` and `mu(B) > mu(C)` in the same arena, that does NOT imply `A` will beat `C` in a comparison event with high probability. Glicko-2 is not transitive in general.

## Concurrency

Multiple inference sessions producing outcome events simultaneously can update the same `(arena, edge)` rows concurrently. The substrate must:

1. **Use row-level locking.** `SELECT ... FOR UPDATE` on the `edge_significance` row before reading current state and applying the update.
2. **Apply updates atomically.** The Glicko update is computed from current state; if another session updates between read and write, retry.
3. **Use serializable transactions.** PostgreSQL's SERIALIZABLE isolation for outcome processing avoids phantom reads.

Pattern: outcome processing acquires a short transaction, locks affected rows, computes new state, writes. Per-outcome processing time is small; long-held locks are unlikely. For batched updates, the lock window is longer but contention is lower (only one batch processor active per partition).

## Cross-references

- Substrate laws governing significance: `10-architecture/01-substrate-laws.md` (Laws 1, 6, 8, 11)
- The arena catalog with full descriptions: `20-technical/10-arenas-catalog.md`
- The Glicko-2 implementation in C: `20-technical/01-native-extension-api.md`
- Provenance catalog with all initial_mu values: `20-technical/13-provenance-catalog.md`
- Inference engine that creates outcome events: `10-architecture/07-inference-engine.md`
- Schema definition: `20-technical/00-schema-reference.md`

## External references

- Glickman, M.E. (2013). *Example of the Glicko-2 system*. <https://glicko.net/glicko/glicko2.pdf>
- Glicko-2 implementations and worked examples: <https://glicko.net/glicko.html>
