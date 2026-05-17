# Arenas Catalog — Full Specification

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers writing code that filters by arena, customers writing recipes, substrate operators tuning trust priors and arena dynamics, anyone reasoning about the substrate's per-arena Glicko-2 dynamics.

---

## What an arena is

An arena is a domain of competition for Glicko-2 ratings. Each arena is a row in `ref.significance_context`. Edges have separate per-arena rating state via `substrate.edge_significance` (μ, φ, σ, games_played); entities have analogous state via `substrate.entity_significance`. Per-tenant divergence is supported via `tenant_arena_rating` (see `10-architecture/16-multi-tenancy.md`).

Arenas are **open-vocabulary**. New arenas can be added at runtime via `arena.add(code, description, parent_arena_code, default_priors)`. Hardcoding the initial set is anti-pattern AP-1; recipes that filter by arena should look up arena codes at runtime, not assume a fixed enumeration.

Per Substrate Law 9, arena ratings are updated by INGESTION (outcome events that are themselves substrate state) and by SCHEDULED batched updates (macro-OODA), never by inline inference writes. This document specifies the seed arena set and the conventions for adding new ones.

## Arena structure

Every arena has:

- **Code** (snake_case identifier, optionally namespaced with `:` separator).
- **Description** (human-readable purpose).
- **Parent arena** (for sub-arenas; see hierarchy section).
- **Default priors** (initial μ, φ, σ for newly-rated edges in this arena; defaults are 1500, 350, 0.06).
- **Default volatility decay** (how fast σ decreases with consistent ratings; default is the Glicko-2 spec value).
- **Rating period** (the batch boundary; default 100 outcomes / 24 hours, whichever comes first).
- **Allowed edge type codes** (which edge types are eligible for ratings in this arena; if `*`, all are eligible).
- **Documentation pointer** (to this document or a successor doc).

## Arena hierarchy

Arenas can be nested via the `:` separator. Sub-arenas inherit their parent's defaults but maintain independent rating state. Examples:

- `model_trust` (top-level)
- `model_trust:huggingface_model:llama-4-maverick` (per-model sub-arena)
- `translation_quality` (top-level)
- `translation_quality:english_to_mandarin` (per-language-pair sub-arena)
- `medical_consensus` (top-level)
- `medical_consensus:oncology` (per-domain sub-arena)

Recipes that need both parent and sub-arena context combine ratings via `default_filter.arena_combine` (see `10-architecture/15-recipe-dsl.md`). Common combinations: `max` (use the most authoritative), `weighted_sum` (blend), `geometric_mean` (penalize when either is low).

## Initial seed arenas

These arenas are seeded by migration `0005_reference_seed`. They are the canonical starting set; recipes that depend on them can assume their existence.

### `lexical_disambiguation`

**Purpose:** Which sense of a polysemous word/lemma fits the current context.

**Allowed edge types:** `has_sense`, `wikt_sense_of_lemma`, `aligned_to_synset`.

**Trust prior interaction:** Princeton WordNet senses receive default-prior weight; Wiktionary senses receive default-prior weight (broader coverage but less curated); UD-attested usage receives outcome-driven updates corroborating or shifting senses based on observed context.

**Default priors:** μ=1500, φ=350, σ=0.06 (standard).

**Worked use:** A traversal seeking the meaning of "bank" in a financial context filters edges in this arena; "bank" → "river bank" sense has lower μ in financial context (no corroborating outcomes from financial corpora) than "bank" → "financial institution" sense.

### `syntactic_role_fitness`

**Purpose:** Which dependency role a token fills in a given syntactic context.

**Allowed edge types:** All `dep_*` edges from UD ingestion.

**Use:** Composition assembly during text generation consults this arena to choose word order matching attested deprel patterns. A generator that needs to attach a noun-phrase as a `dep_obj` rather than `dep_obl` consults the arena to determine which fits the surrounding context's high-rated patterns.

**Default priors:** μ=1500, φ=300, σ=0.05 (slightly lower φ because UD corpora provide many outcome events relative to WordNet senses).

### `translation_quality`

**Purpose:** Cross-lingual alignment quality between a source-language entity and a target-language entity.

**Allowed edge types:** `translation_of`, `translation_link`, `aligned_to_synset`, `cross_language_equivalent`.

**Use:** `transform.translate` cognitive function filters by this arena. Sub-arenas typically include `translation_quality:<source_lang>_to_<target_lang>` for language-pair-specific dynamics.

**Trust prior interaction:** Tatoeba sentence pairs corroborate translation links via outcome events when translations are used and validated; Wiktionary translation tables provide broad coverage with default priors; OMW alignments to Princeton synsets receive default priors plus corroboration as cross-language inferences validate them.

### `model_trust`

**Purpose:** Confidence in a specific ingested model's attestations.

**Allowed edge types:** `beaten_path`, `transformation`, `embedding_similarity`, `hidden_to_token`, `firefly_of_tensor`, all model-derived edges.

**Sub-arenas:** `model_trust:huggingface_model:<model_id>`. Sub-arenas allow per-model competition while sharing the base trust dynamics. A model's sub-arena rating reflects how well its contributions corroborate vs. contradict cross-model consensus over time.

**Outcome interaction:** Voronoi consensus computations (see `10-architecture/12-voronoi-consensus.md`) emit corroboration outcomes — a model whose firefly is close to the consensus centroid receives positive outcomes for its sub-arena; a model whose firefly is an outlier receives negative outcomes.

### `source_authority`

**Purpose:** General reliability of a source across all its attestations, decoupled from any specific arena.

**Allowed edge types:** All; this arena's ratings combine multiplicatively with arena-specific ratings during cost computation.

**Use:** A source whose attestations consistently produce validated outcomes accumulates high `source_authority` rating; future edges from that source enter substrate with priors biased upward by the source's authority.

**Sub-arenas:** `source_authority:<provenance_class>` for fine-grained per-class authority tracking.

### `semantic_relevance`

**Purpose:** Topic fit for a given query context.

**Allowed edge types:** All semantic edges (hypernym/hyponym/meronym/holonym/etc.) plus most cross-modal and cross-lingual edges.

**Use:** Default arena for `inference.converse` when no specific recipe is provided.

**Default priors:** μ=1500, φ=350, σ=0.06.

### `corroboration_strength`

**Purpose:** Cross-source agreement on a relationship.

**Allowed edge types:** All.

**Update mechanism:** When an inference path uses an edge attested by multiple provenance sources, the `corroboration_strength` arena receives a positive outcome event. The more sources corroborate, the higher the rating climbs.

**Use:** Edges with high `corroboration_strength` are "consensus knowledge" — recipes that need high-confidence answers (e.g., medical safety, legal compliance) filter by this arena.

### `frequency_significance`

**Purpose:** Attestation density — how often this relationship appears across corpora and models.

**Allowed edge types:** All.

**Update mechanism:** Each ingestion event that re-attests an existing edge contributes a positive outcome to this arena. High `frequency_significance` ≠ high authority — a frequently-attested but stale claim can have high frequency but low corroboration; recipes choose which signal matters.

### `attention_pattern_confidence`

**Purpose:** How reliable an attention pattern from a model is, based on cross-model corroboration.

**Allowed edge types:** `model_attention_pattern` edges between `word_form` content entities (per `sql/schema/seed/edge_type.sql:84-90`), with sign-aware `positive_evidence`/`negative_evidence` events (P1d 2026-05-14 collapse) and `EdgeRatingEvent` attribution `(Linear, AttentionBlock, {Q,K} or {V,O})` plus layer/head/model_source metadata on the rating event. Cross-model corroboration accumulates as separate (provenance, EdgeRatingEvent-attribution) events on the same edge hash. **The previous "attention_pattern entities" reference is deprecated** per the 2026-05-08 architectural correction — attention patterns are edges between content entities, NOT phantom `attention_pattern` entities. See [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §III and AP-25.

**Use:** Distillation/transformation recipes that depend on attention-pattern accuracy filter by this arena. The Build-a-bear `AttentionQkvLayerSynthesizer` and `AttentionVoLayerSynthesizer` query this arena's mu when synthesizing target attention tensors (per [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md)).

### `morphological_productivity`

**Purpose:** How productive an inflectional or derivational pattern is in a language.

**Allowed edge types:** `has_form`, `inflection_of`, `has_morpheme`, `derives`, `composed_of_grapheme_cluster` (in morphological context).

**Use:** Linguistic analysis queries; recomposer arena recipe for Laplace Linguistics; cross-language morphological comparison studies.

## Operator-curated arenas

These arenas are typically added by substrate operators when they extend the substrate for a customer segment. They are not seeded by default but are common enough to document conventions:

### `pragmatic_register`

**Purpose:** Formal vs. casual usage gradient.

**Allowed edge types:** All; rating reflects the register a relationship is appropriate for.

**Sub-arenas:** `pragmatic_register:formal`, `pragmatic_register:casual`, `pragmatic_register:technical`.

### `temporal_validity:<date_range>`

**Purpose:** Edge validity in a date-bounded period.

**Convention:** date_range is `YYYY-MM-DD_to_YYYY-MM-DD`. Recipes filtering by this arena specify the relevant range; outdated edges have low rating.

### `code_safety`

**Purpose:** Trustworthiness of code patterns (e.g., for security analysis).

**Allowed edge types:** Code-derived edges (`calls_function`, `references_identifier`, etc.).

**Update mechanism:** Linked to outcome events from security audits, CVE corpora ingestion, etc.

### `medical_consensus`

**Purpose:** Agreement among medical sources on a relationship.

**Allowed edge types:** All; primarily used for edges in medical-vocabulary entities.

**Sub-arenas:** `medical_consensus:oncology`, `medical_consensus:cardiology`, `medical_consensus:pharmacology`, `medical_consensus:rare_diseases`, etc.

### `legal_jurisdiction:<region>`

**Purpose:** Edge validity within a specific legal jurisdiction.

**Convention:** region is an ISO 3166-1 alpha-2 country code or a sub-jurisdiction with `:` separator (e.g., `legal_jurisdiction:US:CA` for California).

### `customer:<tenant_id>:<purpose>`

**Purpose:** Per-tenant private arenas. Visibility scoped to the tenant.

**Convention:** purpose describes the tenant's use case (e.g., `customer:acme-corp:product_taxonomy`, `customer:acme-corp:internal_glossary`).

## Operator-only arenas (substrate-internal)

These arenas are managed by the substrate operator and not exposed to customers:

### `internal:macro_ooda_priorities`

Tracks macro-OODA's prioritization decisions for ingestion proposals. Higher rating = higher priority.

### `internal:audit_integrity`

Tracks substrate-internal audit-chain verification outcomes.

### `internal:substrate_health`

Tracks operational health metrics (replication lag, ingestion-pipeline backlog, etc.). Outcome events come from substrate-internal monitoring.

## Arena addition workflow

To add a new arena:

1. Decide code, parent (if any), purpose, allowed edge types, default priors.
2. Run `arena.add(code, description, parent_arena_code, default_priors)`.
3. The function emits an `arena` entity with provenance from the substrate operator (or from an authorized customer for tenant-scoped arenas).
4. Update this catalog (or, for tenant arenas, document in the tenant's recipe library).
5. If the arena is operator-curated, add to `40-process/checklists/04-arena-addition-checklist.md`.

The add operation is itself an audit-trace-emitting operation. Arena removal is uncommon (typically arenas are RETIRED — frozen but retained — rather than removed; see `10-architecture/18-continuous-learning-loop.md`).

## Per-tenant arena divergence

Per `10-architecture/16-multi-tenancy.md`, every arena maintains both a canonical (cross-tenant aggregate) view and per-tenant divergent views. The substrate's interpretation of "the rating in arena X" depends on the calling tenant:

- Tenants with light usage: their queries see the canonical view; their outcomes contribute to canonical aggregation.
- Tenants with heavy usage in an arena: their per-tenant view diverges; their queries see their own view; their outcomes feed both their view and the canonical aggregate (weighted by tenant authority).

Recipes can specify `arena_view: "canonical"` or `arena_view: "tenant"` to override the default (which is "tenant when divergence is significant; canonical otherwise"). The threshold is per-arena and operator-controlled.

## Worked example

A medical-research customer (tenant: ACME) configures the following arenas for their use cases:

- `medical_consensus:oncology` — operator-curated arena.
- `customer:acme-corp:internal_terminology` — tenant-private.
- `customer:acme-corp:product_taxonomy` — tenant-private.

ACME's recipes use the cascade:

```jsonc
"default_filter": {
  "arenas": [
    "customer:acme-corp:internal_terminology",
    "medical_consensus:oncology",
    "corroboration_strength"
  ],
  "arena_combine": "weighted_sum",
  "arena_weights": {
    "customer:acme-corp:internal_terminology": 0.5,
    "medical_consensus:oncology": 0.3,
    "corroboration_strength": 0.2
  }
}
```

This recipe blends three arenas: ACME's private terminology dominates (the customer's domain expertise), medical-oncology consensus contributes (industry-wide validation), and corroboration strength as a sanity check (cross-source agreement).

Over 6 months of ACME's usage:

- Their internal-terminology arena diverges from canonical because their outcomes drive its evolution.
- Medical-oncology canonical view shifts based on cumulative outcomes from all medical-research tenants — ACME contributes, but so do others.
- ACME's outcomes also feed the canonical view, weighted by ACME's authority in this domain (computed via their cumulative outcome reliability).

A new medical-research tenant onboarded after ACME inherits ACME's contributions to the canonical view (subject to legal access — per data residency and sharing terms) but starts with default priors on their own private arenas. The substrate has, structurally, become better at medical-oncology because of ACME's usage.

## What arenas are NOT

- **Not topics.** An arena is a competitive landscape for ratings, not a topic taxonomy. Topics are encoded as entities and edges in their own right; arenas are about HOW ratings evolve, not WHAT is rated.
- **Not tags.** An arena is the binding context for rating dynamics; tagging an edge with an arena code is meaningless without the underlying rating state.
- **Not access control.** Arenas don't enforce visibility (multi-tenancy provenance does that). All arenas are visible to all tenants who have rights to see the underlying edges.
- **Not optional.** Every rated edge has at least one arena binding. The default arena for unspecified edges is `semantic_relevance`.

## Cross-references

- Significance pillar (Glicko-2 substrate, math foundation): `10-architecture/04-significance-glicko.md`
- Continuous learning loop (how arena ratings evolve over time): `10-architecture/18-continuous-learning-loop.md`
- Multi-tenancy (per-tenant divergence): `10-architecture/16-multi-tenancy.md`
- Recipe DSL (how recipes specify arena filters): `10-architecture/15-recipe-dsl.md`
- Schema (column definitions for arena tables): `20-technical/00-schema-reference.md`
- Anti-pattern AP-1 (arena cherry-picking, hardcoding): `40-process/01-anti-patterns.md`
- Arena-addition checklist: `40-process/checklists/04-arena-addition-checklist.md`

## External references

- Glicko-2 specification (Glickman 2012): <http://www.glicko.net/glicko/glicko2.pdf>
