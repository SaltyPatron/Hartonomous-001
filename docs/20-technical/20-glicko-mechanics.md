# Glicko-2 Mechanics — Update Math, Volatility, Implementation

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the Glicko-2 update functions in `hartonomous_pg`, anyone debugging arena dynamics, anyone reasoning about the substrate's rating mathematics from first principles.

---

## Why Glicko-2

The substrate uses Glicko-2 (Glickman 2012) rather than Elo, Glicko-1, TrueSkill, or other rating systems for these reasons:

- **Volatility tracking.** Glicko-2 maintains a volatility parameter σ that captures whether an edge's rating has been changing rapidly recently. High volatility means the substrate should expect more change; low volatility means the rating is settled. This handles concept drift natively (see `10-architecture/18-continuous-learning-loop.md`).
- **Rating periods.** Glicko-2 prescribes batched updates over rating periods rather than after-every-game updates. This matches the substrate's outcome-event architecture: outcomes accumulate and are applied in batches, not incrementally.
- **Mathematical rigor.** Glicko-2 has a clear theoretical basis (Bayesian updates over a normal distribution of skills with a separate volatility process). The substrate inherits its calibration properties (rating intervals are interpretable as confidence intervals).
- **Single-pool dynamics with multi-arena scope.** The substrate runs Glicko-2 independently per arena, so each arena's competitive dynamics don't contaminate others.

This document specifies the math in detail and the substrate's implementation choices.

## Notation

Glicko-2 uses two scales:

- **Glicko-1 scale:** `R` for rating (centered at 1500, spread ~250–400), `RD` for rating deviation (uncertainty), `σ` for volatility.
- **Glicko-2 scale:** `μ = (R - 1500) / 173.7178`, `φ = RD / 173.7178`, σ unchanged. The factor 173.7178 = 400 / ln(10) makes the formulas clean.

The substrate stores Glicko-1 scale values in `mu`, `phi`, and `sigma` columns of `entity_significance` and `edge_significance`. The Glicko-2 update converts to Glicko-2 scale internally, applies updates, and converts back.

The `games` column counts the cumulative number of outcome events applied to this rating. It is used for diagnostics and for some recipe-specific weighting; it does not enter the Glicko-2 math directly.

## Per-rating-period update

For an edge with current state (μ, φ, σ) at the start of a rating period, given a batch of N outcomes from this period, the update procedure:

### Step 1 — convert to Glicko-2 scale

```
μ = (R - 1500) / 173.7178
φ = RD / 173.7178
```

### Step 2 — for each outcome i, identify the opponent's parameters

In the substrate, an "opponent" is a synthetic rating representing the outcome's authority and direction:

- **Validated outcome** from a high-authority source: opponent has high rating, opponent's φ low. The match is "the edge tied (or nearly so) against a strong opponent" → drives μ upward.
- **Refuted outcome** from a high-authority source: opponent has high rating, opponent's φ low, but the result score is 0. The match is "the edge lost decisively" → drives μ downward.
- **Partial outcome:** result score 0.5; opponent calibrated to the trace's primary arena's typical rating.
- **Corroborated outcome** (from cross-source ingestion): opponent rating equals the corroborating source's authority; result depends on whether the corroboration confirms the existing edge's direction.

The mapping from outcome event to (opponent_R, opponent_RD, score) is implemented per the `outcome_attribution` recipe; the default mapping is documented in `10-architecture/18-continuous-learning-loop.md`.

For each outcome, convert the opponent's Glicko-1 (R_opp, RD_opp) to Glicko-2 (μ_opp, φ_opp).

### Step 3 — compute g(φ_opp) and E for each outcome

```
g(φ) = 1 / sqrt(1 + 3·φ² / π²)
E(μ, μ_opp, φ_opp) = 1 / (1 + exp(-g(φ_opp)·(μ - μ_opp)))
```

`g(φ)` is a damping factor that reduces the impact of opponents whose own rating uncertainty is high. `E` is the expected score of the edge against this opponent (a value in (0, 1)).

### Step 4 — compute estimated variance v

```
v = (Σ_i g(φ_opp_i)² · E_i · (1 - E_i))⁻¹
```

`v` is the variance of the underlying skill estimate given the batch. Larger v = batch provides less information; smaller v = batch is informative.

### Step 5 — compute estimated improvement Δ

```
Δ = v · Σ_i g(φ_opp_i) · (s_i - E_i)
```

where `s_i` is the actual outcome score (1 = win, 0.5 = draw, 0 = loss). `Δ` is the rating-improvement estimate before incorporating prior uncertainty.

### Step 6 — update volatility σ' via iteration

This is the most complex step. The new volatility σ' is computed by Newton-Raphson iteration on a function f(x):

```
f(x) = (e^x · (Δ² - φ² - v - e^x)) / (2·(φ² + v + e^x)²) - (x - a) / τ²
```

where `a = ln(σ²)` and `τ` is the system constant (Glicko-2's hyperparameter; substrate uses τ = 0.5; rationale below).

Find x* such that f(x*) = 0 via Illinois algorithm (variant of regula falsi):

1. Set A = a (initial bracket left endpoint).
2. If Δ² > φ² + v: set B = ln(Δ² - φ² - v); else iterate B = a - τ until f(B) < 0.
3. f_A = f(A), f_B = f(B).
4. While |B - A| > ε (default ε = 0.000001):
   - C = A + (A - B)·f_A / (f_B - f_A)
   - f_C = f(C)
   - If f_C · f_B < 0: A = B, f_A = f_B; B = C, f_B = f_C.
   - Else: f_A = f_A / 2; B = C, f_B = f_C.
5. σ' = exp(A / 2).

The iteration typically converges in 5–15 steps. The algorithm is implemented in C in `hartonomous_pg::glicko2_update_volatility`. Substrate's choice of τ = 0.5 follows Glickman's recommendation for high-stakes rated systems with episodic-update dynamics.

### Step 7 — update φ to φ_star

```
φ_star = sqrt(φ² + σ'²)
```

This is the rating deviation expanded to account for elapsed time / volatility before incorporating the new outcomes.

### Step 8 — compute new φ' and μ'

```
φ' = 1 / sqrt(1/φ_star² + 1/v)
μ' = μ + φ'² · Σ_i g(φ_opp_i) · (s_i - E_i)
```

### Step 9 — convert back to Glicko-1 scale

```
R' = 173.7178·μ' + 1500
RD' = 173.7178·φ'
```

Store `(R', RD', σ')` as the edge's new `(mu, phi, sigma)`. Increment `games` by the batch size N. Update `last_update` timestamp.

## No-outcome rating period

If a rating period passes without any outcomes for an edge, the math reduces to:

```
φ' = sqrt(φ² + σ²)
μ' = μ
σ' = σ
```

The rating itself doesn't change, but the rating deviation φ EXPANDS to reflect the elapsed time of uncertainty. Edges that go un-updated for many rating periods accumulate large φ; the next outcome event will move μ more than it would for a recently-updated edge.

This is implemented as a passive expansion: rather than applying to every edge each period (which would be O(total edges) per period), the substrate records `last_update` and computes the effective `phi_star` lazily when the edge is queried or updated.

## Substrate's hyperparameters

| Parameter | Value | Rationale |
|---|---|---|
| τ (system constant) | 0.5 | Glickman's recommendation for systems where ratings can change due to factors beyond the in-system outcomes (concept drift, source corpus updates). |
| Initial μ | 1500 | Glicko-1 standard center. |
| Initial φ | 350 | Glicko-1 standard "unrated" deviation. |
| Initial σ | 0.06 | Glickman's suggested default. |
| Convergence ε for σ iteration | 1e-6 | Sufficient precision; iteration converges fast. |
| Rating period default | 100 outcomes / 24 hours, whichever first | Matches outcome event throughput typical of substrate workloads. |

These defaults apply at arena creation. Arena administrators (substrate operators) can override per-arena via `arena.add(..., default_priors)`.

## Per-tenant divergence

When a tenant's per-tenant rating diverges from the canonical view (per `10-architecture/16-multi-tenancy.md`), the substrate runs Glicko-2 updates SEPARATELY:

1. Tenant outcomes update the tenant's per-tenant rating in `tenant_arena_rating`.
2. The same outcomes (weighted by tenant authority) ALSO contribute to a canonical-update batch.
3. At the rating period boundary, the canonical update applies the weighted combination of all tenants' contributions.

The math is identical to the per-rating-period update; the difference is in WHICH ratings the outcomes feed.

Outcome-attribution weights are computed at outcome ingestion time, recorded in the outcome event, and frozen — applying the same outcome twice (idempotency) is mathematically and procedurally guaranteed because the outcome's contribution is fixed.

## Implementation in `hartonomous_pg`

The Glicko-2 update is implemented as:

```c
typedef struct {
    double mu_glicko2;
    double phi_glicko2;
    double sigma;
} glicko2_state;

typedef struct {
    double mu_opp_glicko2;
    double phi_opp_glicko2;
    double score;
} glicko2_outcome;

glicko2_state glicko2_update(
    const glicko2_state *current,
    const glicko2_outcome *outcomes,
    size_t n_outcomes,
    double tau
);

double glicko2_update_volatility(
    double sigma_old,
    double phi,
    double v,
    double Delta,
    double tau,
    double epsilon
);
```

The implementation is exposed to PL/pgSQL via:

```sql
CREATE FUNCTION hartonomous.glicko2_update(
    current_mu float8,
    current_phi float8,
    current_sigma float8,
    opponents_mu float8[],
    opponents_phi float8[],
    scores float8[]
) RETURNS TABLE (new_mu float8, new_phi float8, new_sigma float8);
```

Performance: a single Glicko-2 update over a batch of 100 outcomes runs in under 100 microseconds on commodity hardware. The dominant cost in batched-update jobs is row I/O, not the Glicko math.

## Worked example

Edge: `(metformin) -[treats]-> (type-2-diabetes)` in arena `medical_consensus:endocrinology`.

Current state: μ_glicko1 = 1700, RD = 80, σ = 0.05. Convert to Glicko-2:

```
μ = (1700 - 1500) / 173.7178 ≈ 1.151
φ = 80 / 173.7178 ≈ 0.460
```

Rating period batch of 5 outcomes:

| # | Outcome class | Source | μ_opp_glicko1 | RD_opp | Score |
|---|---|---|---|---|---|
| 1 | validated | DrugBank | 1900 | 50 | 0.5 |
| 2 | corroborated | UpToDate clinical reference | 1850 | 60 | 0.5 |
| 3 | validated | Customer (high-authority oncology research lab) | 1800 | 70 | 0.5 |
| 4 | partial | NIH consumer health summary | 1600 | 100 | 0.4 |
| 5 | corroborated | Recent NEJM meta-analysis ingested | 1950 | 40 | 0.6 |

Convert each opponent to Glicko-2 scale and apply Steps 3–9.

After computation:
- v ≈ 0.0234 (low — informative batch)
- Δ ≈ 0.0123
- σ' ≈ 0.0496 (slight decrease — recent updates are consistent)
- φ' ≈ 0.341 (reduced uncertainty)
- μ' ≈ 1.156 (small upward shift)

Convert back to Glicko-1:
- R' = 173.7178 · 1.156 + 1500 ≈ 1701
- RD' = 173.7178 · 0.341 ≈ 59
- σ' ≈ 0.0496

The edge moved 1701 from 1700 (small confirmation), RD tightened from 80 to 59 (significantly more confident), σ dropped slightly. The rating now reflects the cumulative authority of 5 confirming outcomes.

## Diagnostics

The substrate exposes diagnostic queries for understanding rating dynamics:

```sql
-- Per-arena rating distribution
SELECT
    percentile_cont(ARRAY[0.10, 0.25, 0.50, 0.75, 0.90])
    WITHIN GROUP (ORDER BY mu) AS mu_quantiles,
    percentile_cont(ARRAY[0.10, 0.25, 0.50, 0.75, 0.90])
    WITHIN GROUP (ORDER BY phi) AS phi_quantiles
FROM substrate.edge_significance
WHERE context_type_id = (SELECT id FROM ref.significance_context WHERE code = 'medical_consensus');

-- Edges with anomalously high volatility (concept drift candidates)
SELECT edge_type_id, edge_hash, sigma
FROM substrate.edge_significance
WHERE context_type_id = (SELECT id FROM ref.significance_context WHERE code = 'medical_consensus')
  AND sigma > 0.10
ORDER BY sigma DESC
LIMIT 100;

-- Edges that haven't been updated in >90 days (potentially stale)
SELECT edge_type_id, edge_hash, mu, phi, last_update
FROM substrate.edge_significance
WHERE last_update < now() - interval '90 days'
  AND context_type_id = (SELECT id FROM ref.significance_context WHERE code = 'medical_consensus')
ORDER BY last_update ASC
LIMIT 100;
```

These diagnostics feed macro-OODA's drift-detection and stale-arena flagging.

## Boundary cases

- **All draw scores in the batch:** μ' ≈ μ; φ' tightens slightly; σ' updates per the iteration.
- **Single overwhelming-authority opponent (very low φ_opp, far-from-μ):** v becomes small (informative); Δ can be large; rating moves significantly.
- **Empty batch (no outcomes for the rating period):** apply the no-outcome update. Only φ expands by σ.
- **Numerical edge cases:** μ values approaching the edge of the float8 range, σ approaching 0 or very large. The implementation includes guards: σ floored at 1e-6, μ clamped to [-50, 50] in Glicko-2 scale (corresponds to roughly [-7150, 10150] in Glicko-1 — far beyond any practical value).

## What Glicko-2 doesn't do

- **Doesn't predict outcomes.** Glicko-2 is descriptive: given outcomes, update beliefs. Edge ratings are not used to PROBABILISTICALLY PREDICT which paths an inference will take; A* uses ratings as cost weights deterministically.
- **Doesn't multi-rate within a period.** All outcomes in a rating period are applied simultaneously, not sequentially. Outcome ordering within the period doesn't affect the final state.
- **Doesn't handle multi-team matches natively.** The substrate's "match" is between an edge and a synthetic opponent; the substrate maps each outcome to a synthetic opponent rather than rating multi-edge contests directly. Per-edge attribution within a path is handled in pre-processing (see `10-architecture/18-continuous-learning-loop.md`).

## Cross-references

- Glicko-2 paper (Glickman 2012): <http://www.glicko.net/glicko/glicko2.pdf>
- Significance pillar: `10-architecture/04-significance-glicko.md`
- Continuous learning loop (where outcomes come from): `10-architecture/18-continuous-learning-loop.md`
- Arenas catalog (per-arena hyperparameters): `20-technical/10-arenas-catalog.md`
- Schema (rating tables): `20-technical/00-schema-reference.md`
- Multi-tenancy (per-tenant divergent ratings): `10-architecture/16-multi-tenancy.md`
- Native extension API (the C implementation): `20-technical/01-native-extension-api.md`
