# Infrastructure vs Substrate — The Two-Layer Discipline

**Status**: PARTIAL - layer discipline is current; classification/sequence framing is stale

Use this document for the infrastructure-vs-substrate distinction, but do not cite sections that treat classification consensus as only junction-table infrastructure or mention `substrate.sequence`. Current AP-8 framing: classification vocabulary codes can be content-hashed substrate entities reached by typed attestation edges, with authoritative consensus on `substrate.edge_significance`; junction tables are analytics caches. Current composition ordering lives in GeometryZM physicality vertex streams, not a sequence table.

Hartonomous keeps two fundamentally different kinds of data strictly separated: **app-layer infrastructure** (reference vocabularies and junction tables — cached judgment, rebuildable, microsecond-lookup surfaces) and **substrate content** (entities, edges, physicality, significance, sequence — ingested digital content, content-addressed, deterministic, irreducible). Collapsing them destroys both layers' guarantees. This document specifies the line, shows what lives on each side, and walks through concrete probe queries that use both layers together.

---

## Why the split is mandatory

Every query the substrate answers falls into one of two classes:

1. **Classification lookups** — "Is this word a noun? Which language is this in? What part of speech does this form bear? Which register does this lemma belong to?" These must answer in **microseconds** because they are invoked at every level of decomposition, on every entity, on every forward pass. The substrate performs millions of them per ingestion batch and hundreds of thousands of them per inference query. They must be one indexed JOIN, not a graph traversal.

2. **Content retrieval and reasoning** — "What does this specific attested Wiktionary citation say? What is the Fréchet distance between these two conversation trajectories? Which edge paths connect this lemma to that synset? What is the centroid of this document?" These are content questions whose answers live in the substrate proper. They are slower but still tractable because of partitioning and GiST indexes.

These two workloads have incompatible design pressures:

| Workload | Requires | Incompatible with |
|---|---|---|
| Classification lookups | Small bounded-cardinality tables, tight composite indexes, stable keys, pre-seeded rows | Growing row counts, partitioned tables, BLAKE3-keyed lookups, DAG traversal |
| Content retrieval | Content-addressed identity, DAG composition, geometric physicality, Glicko-rated significance per row | Aggressive denormalization, bounded vocabulary, cached judgments |

Putting them in the same table (or worse, in the same conceptual layer) damages both. Classification lookups slow down because the table has billions of substrate rows. Content queries become confusing because classification metadata mixes with content. Worse, the discipline that lets the substrate be deterministic (Law #6) is violated if classification rows drift into the Merkle DAG.

The separation is not a preference. It is a correctness property.

---

## What belongs to the app-layer (infrastructure)

The app layer holds **classification vocabularies** and **per-classification evidence**. Specifically:

### Reference tables (migrations 0004, 0016, 0017)

These are small, bounded, mostly write-once:

| Table | Cardinality | Source |
|---|---|---|
| `substrate.entity_type` | 25 | Phase 1 seed (`0005`) |
| `substrate.edge_type` | 33–34 | Phase 1 seed + `0037` |
| `substrate.edge_role` | 7 | Phase 1 seed |
| `substrate.physicality_type` | 13 | Phase 1 seed |
| `substrate.provenance` | 10 (seed) + N (added per corpus) | Phase 1 + corpus ingestion |
| `substrate.significance_context` | 10 | Phase 1 seed |
| `substrate.pos` | 17 (Universal Dependencies POS tag set) | Phase 1 seed |
| `substrate.deprel` | ~40 (Universal Dependencies relation set) | Phase 1 seed |
| `substrate.morph_feature` | Hundreds (UniMorph-aligned) | Phase 1 seed |
| `substrate.sense` | Reference sense categories | Phase 1 seed |
| `substrate.lexname` | 45 (WordNet lexicographer files) | Phase 1 seed |
| `substrate.semantic_relation_type` | WordNet relation vocabulary | Phase 1 seed |
| `substrate.general_category` | 30 (Unicode General_Category) | Phase 1 seed |
| `substrate.script` | ~200 (Unicode scripts) | Phase 1 seed |
| `substrate.block` | ~300 (Unicode blocks) | Phase 1 seed |
| `substrate.break_property` | UAX #29 boundary classes | Phase 1 seed |
| `substrate.language` | ~8000 (ISO 639-3 post-`0016`/`0017`) | Phase 1 + ISO 639 seed |
| `substrate.tensor_role` | Tensor role vocabulary | Phase 1 seed |
| `substrate.architecture_class` | Model architecture classes | Phase 1 seed |

**Properties**:
- Bounded cardinality (at most thousands of rows per table).
- Stable integer primary keys.
- Text codes (`code` columns) are stable identifiers. Code → id lookups are cached.
- Written only at seed time or when new classification vocabulary is explicitly added.
- Referenced by BIGINT FKs from substrate tables.

### Junction tables (migration 0007)

These are **per-classification evidence** tables. They carry confidence ratings (Glicko-2 where rated) for "is this entity a member of this class":

| Table | Columns | Glicko-2? | Semantics |
|---|---|---|---|
| `entity_pos` | `(entity_id, pos_id, mu, sigma, volatility, games)` | ✅ | "Is this lemma a noun? verb? adjective?" rated per POS. |
| `entity_sense` | `(entity_id, sense_id, mu, sigma, volatility, games)` | ✅ | "Does this word_form bear this sense?" rated per sense. |
| `entity_language` | `(entity_id, language_id)` | ❌ | Language membership is a fact, not a judgment. |
| `entity_morph_feature` | `(entity_id, morph_feature_id)` | ❌ | Morphological feature attachment. |
| `codepoint_property` | `(entity_id, general_category_id, script_id, block_id, gcb_id, wb_id, sb_id, lb_id)` | ❌ | Per-codepoint Unicode properties. |
| `model_architecture_class` | `(entity_id, architecture_class_id)` | ❌ | Model architecture membership. |
| `tensor_tensor_role` | `(entity_id, tensor_role_id)` | ❌ | Tensor role assignment. |
| `pattern_deprel` | `(entity_id, deprel_id, mu, sigma, volatility, games)` | ✅ | "Does this dependency pattern bear this relation?" rated per deprel. |

Junction tables where Glicko-2 applies carry the confidence of the assignment *as a classification*. They do NOT rate the content itself. They rate **"this classification is true of this entity in this context, to this degree."**

### What the app layer answers in microseconds

The app layer answers classification questions with single indexed JOINs:

```sql
-- "Is 'rake' a noun, verb, or both?"
SELECT p.code, ep.mu, ep.sigma, ep.games
FROM substrate.entity_pos ep
JOIN substrate.pos p ON p.id = ep.pos_id
WHERE ep.entity_id = :rake_entity_id;

-- "What language is this sentence in?"
SELECT l.iso639_3
FROM substrate.entity_language el
JOIN substrate.language l ON l.id = el.language_id
WHERE el.entity_id = :sentence_entity_id;

-- "What properties does this codepoint have?"
SELECT gc.code, s.code, b.code
FROM substrate.codepoint_property cp
JOIN substrate.general_category gc ON gc.id = cp.general_category_id
JOIN substrate.script s ON s.id = cp.script_id
JOIN substrate.block b ON b.id = cp.block_id
WHERE cp.entity_id = :codepoint_entity_id;
```

Every one of these is O(log n) with composite indexes. None of them touches substrate content. None of them traverses a DAG. They are cheap gates.

### Property: the app layer is rebuildable from seeds

The entire app layer can be dropped and rebuilt from:
1. The Phase 1 seed migration (`0005`).
2. The ISO 639 seed (`0017`).
3. Any per-corpus provenance additions.
4. Re-running the decomposers that populate junction tables.

No app-layer data is **irreplaceable content**. This is how it differs from the substrate.

---

## What belongs to the substrate (content)

The substrate holds **ingested digital content**, content-addressed, irreducible, and deterministically produced. Specifically:

### Substrate tables (migration 0006)

| Table | Contains | Growth |
|---|---|---|
| `substrate.entity` | Content atoms (codepoints, tokens, tensors, etc.) and their compositions (word_forms, sentences, documents, tensors). Each row has a BLAKE3 content hash. | Unbounded, grows with distinct content ingested. |
| `substrate.edge` + `substrate.edge_member` | Typed n-ary relations among entities, with role-ordered members and trajectory geometry. | Grows with attested relations. |
| `substrate.physicality` | Geometric position and trajectory data for entities. One row per (entity, physicality_type). GiST-indexed. | Grows with entities that have geometry. |
| `substrate.significance` | Per-entity and per-edge Glicko-2 ratings, scoped by significance context. | Grows with rated substrate objects. |
| (composition ordering — NOT a separate table) | Ordered composition relationships live in the parent's `physicality_contour` LINESTRINGZM vertex stream. Each vertex mantissa-packs `(child.hash_bits_0_51, bb_pack_ordinal_rle(ordinal, rle_count), child.hash_bits_52_103, metadata)`. The geometry IS the indexed child manifest; reverse-resolve via `substrate.entity_by_hash_prefix`. There is no `substrate.sequence` table. | Grows with compositions (one LINESTRINGZM row per composition per content_hash realization). |

**Properties**:
- Content-addressed: every row's identity is BLAKE3 over content only.
- Deterministic: Law #6 requires byte-identical state for byte-identical inputs.
- DAG-structured: shared sub-content is shared via FK reference, not duplicated.
- Partitioned by type for scale management.
- Geometric: physicality carries GEOMETRY4D (or PostGIS GeometryZM for 2D-plus-payload types) indexed by 4D envelope.

### What the substrate answers

Substrate queries reason over content:

```sql
-- Find all sentences whose centroid is within Fréchet-distance 5.0 of a target trajectory.
SELECT e.id, ST_FrechetDistance4D(p.geom, :target_trajectory) AS d
FROM substrate.entity e
JOIN substrate.physicality p ON p.entity_id = e.id
WHERE e.entity_type_id = :sentence_type
  AND ST_FrechetDistance4D(p.geom, :target_trajectory) < 5.0
ORDER BY d
LIMIT 20;

-- Retrieve all edges of type `has_sense` out of this lemma, with Glicko ratings.
SELECT e.id, e.edge_type_id, s.mu, s.sigma, s.games
FROM substrate.edge e
JOIN substrate.edge_member m ON m.edge_id = e.id AND m.edge_role_id = :source
LEFT JOIN substrate.significance s ON s.edge_id = e.id AND s.context_type_id = :lexical_disambiguation
WHERE m.entity_id = :lemma_id AND e.edge_type_id = :has_sense;

-- Reconstruct a text_composition from its substrate state.
SELECT substrate.recompose_text(:text_entity_id);
```

These queries are tractable but not microsecond. A\* traversal is polynomial in path length and budget; geometric queries are logarithmic in indexed size. The substrate is fast but not cheap-gate fast.

---

## Glicko-2 on junctions vs Glicko-2 on substrate: the distinction

Both surfaces carry Glicko-2 ratings. They rate different things:

| Rating location | What it rates |
|---|---|
| `substrate.significance(entity_id=X)` | "How trustworthy is this content?" — e.g., a Wiktionary citation's overall reliability. |
| `substrate.significance(edge_id=X)` | "How strong is this attested relation?" — e.g., `has_sense(lemma→synset)` with evidence strength. |
| `entity_pos(entity_id, pos_id).mu` | "How confidently does this lemma bear this POS tag in the attested corpora?" — a classification judgment. |
| `entity_sense(entity_id, sense_id).mu` | "How strongly does this word_form attach to this sense in attested use?" |
| `pattern_deprel(entity_id, deprel_id).mu` | "How strongly does this dependency pattern bear this deprel label?" |

The difference is between **rating content** (substrate.significance) and **rating classification judgments** (junction Glicko-2). Both are valid. Both update on use. They serve different queries.

A common anti-pattern is merging these into one surface. Don't. The substrate's significance rates *what is there*; the junction's Glicko rates *what we say about what is there*.

---

## Query composition: cheap gate + deep read

Well-formed inference queries use BOTH layers:

1. **Junction prune**: resolve the candidate space to a small set via microsecond JOINs on the app layer.
2. **Substrate traverse**: A\* / geometric / recomposition queries over the candidates, using content-addressed substrate data.

Example: "Answer a question about 'dog' as a verb."

```sql
-- Step 1: Cheap gate — does 'dog' even have a verb classification?
SELECT ep.mu, ep.sigma, ep.games
FROM substrate.entity_pos ep
JOIN substrate.pos p ON p.id = ep.pos_id
WHERE ep.entity_id = :dog_entity_id
  AND p.code = 'VERB';

-- Step 2: If yes (and possibly further constrained by significance), descend into substrate.
-- Find attested verb-sense usage.
SELECT e.id, s.mu
FROM substrate.edge e
JOIN substrate.edge_member m ON m.edge_id = e.id AND m.edge_role_id = :source
JOIN substrate.entity_sense es ON es.entity_id = :dog_verb_lemma_id
JOIN substrate.significance s ON s.edge_id = e.id
WHERE m.entity_id = :dog_entity_id
  AND e.edge_type_id IN (:has_sense, :has_gloss, :has_example);
```

The junction answered "does this classification exist and how strongly" in microseconds. The substrate answered "what is the attested content that instantiates this classification" via the edge graph.

Skipping step 1 and going straight to substrate is wasteful — you'd A\*-traverse huge chunks of the graph to discover that no verb sense exists. Skipping step 2 after step 1 is insufficient — you'd only know *that* a verb sense exists, not what it means or what corroborates it.

---

## Probe case study 1: "rake the rakes"

This sentence exercises both layers because the same surface form appears twice with different POS bindings.

### App-layer resolution (microseconds)

```sql
-- Resolve 'rake' to its word_form entity (or lemma if normalized).
SELECT id FROM substrate.entity WHERE hash = blake3('rake') AND entity_type_id = :word_form;

-- Retrieve POS classifications.
SELECT p.code, ep.mu, ep.games
FROM substrate.entity_pos ep
JOIN substrate.pos p ON p.id = ep.pos_id
WHERE ep.entity_id = :rake_entity_id;
-- Returns: NOUN (mu=high, games=high), VERB (mu=mid, games=mid)
```

Both POS exist, both are rated. The sentence is legal.

### Substrate-layer observation

The sentence decomposes as a `text_composition` whose children — encoded as mantissa-packed vertices in the text_composition's LINESTRINGZM physicality — include the `rake` word-form entity **at two distinct ordinal positions** (two vertices whose X+Z mantissa halves resolve to the same `rake` hash via `substrate.entity_by_hash_prefix`, but whose Y mantissa carries different `bb_pack_ordinal_rle(ordinal, rle_count)` values). Because identity is content-only, it is the same entity referenced twice, not two entities.

The sentence's syntactic parse (from UD decomposition) attaches one `rake` reference to a `VERB` token role and the other to a `NOUN` token role. The `substrate.edge` rows carrying `has_lemma` and dependency relations include both role attachments.

The sentence's `linestring4d` in the GEOMETRY4D column (per `specs/native/geometry4d-composition.md`) traces through its word-form centroids. Because `rake` has exactly one centroid (content-addressed), the trajectory returns to the same 4D point at two different ordinal positions.

### The geometric weirdness

The trajectory `linestring4d` has **a loop back to the same vertex**. This is detectable as:

- Low dispersion despite nontrivial length.
- A self-intersection at the `rake` centroid.
- A Fréchet distance to "typical" sentence trajectories that flags the cyclicity.

**This is what "weird to say" means, operationalized.** The sentence is legal (the app layer confirms both POS are valid) but geometrically degenerate (the substrate shows the trajectory folds back). The substrate detects the weirdness as a geometric property, not as a rule violation.

---

## Probe case study 2: "dog the door"

This sentence exercises the app layer to identify a low-rated verb sense, then exercises the substrate to find the attested nautical meaning.

### App-layer resolution

```sql
SELECT p.code, ep.mu, ep.games
FROM substrate.entity_pos ep
JOIN substrate.pos p ON p.id = ep.pos_id
WHERE ep.entity_id = :dog_entity_id;
-- Returns: NOUN (mu=very_high, games=huge), VERB (mu=low, games=small)
```

The verb sense exists but is rated much lower than the noun sense.

### Substrate traversal

Starting from `dog` (word_form) with verb POS selection, A\* across `has_lemma → has_sense → has_gloss` edges reaches a Wiktionary sense glossed as "to fasten with dogs (metal hardware)." The `substrate.significance` on this edge is rated by use; starting prior comes from Wiktionary's `trust_prior_mu`.

The `dog_v` lemma has `has_example` edges to `tatoeba_sentence` entities where `dog` appears in nautical contexts ("dog the hatches"). These attested examples give evidentiary weight to the verb sense.

The substrate returns a named path:

```
dog (word_form, id=X)
  → has_lemma → dog_v (lemma, id=Y)
    → has_sense → wikt_sense:fasten_with_hardware (id=Z)
      → has_gloss → "to fasten with dogs" (text_composition, id=W)
      → has_example → "dog the hatches" (tatoeba_sentence, id=V)
```

Each edge has a Glicko rating. The path is auditable.

### What the composition accomplishes

The app layer flagged the unusualness (verb-dog has low μ). The substrate resolved it (the verb sense has a specific, attested, nautical meaning with provenance). Neither answer alone is sufficient. Together they explain both *why the sentence is parseable* and *what it means*.

---

## Probe case study 3: "scurvy dog"

This probe specifically exercises the `lexicalized_compound` edge type (migration `0037`) and the coexistence of compositional and whole-form representations.

### App-layer resolution

Both `scurvy` and `dog` have POS classifications (app layer):

```sql
-- 'scurvy': {ADJ, NOUN}
-- 'dog':    {NOUN, VERB}
```

Nothing in the app layer says "insult" by default. Insult-register would require an `entity_pragmatic_register` junction (not yet in the base schema; see `specs/engine/substrate-governance.md` for the proposed extension).

### Substrate: the lexicalized compound edge

Migration `0037` introduces `lexicalized_compound` as a structural edge type with `source = lemma` and `target = word_form`. If Wiktionary ingests `scurvy_dog` as an attested lemma, the substrate holds:

1. **`scurvy_dog`** as its own `lemma` entity, with its own BLAKE3 hash. It has its own `has_sense` edge to a wikt_sense glossed as "(nautical, derogatory) a contemptible person." It has its own centroid in the 4D frame, derived from its own attested usage trajectories.
2. **`scurvy` + `dog`** as two separate `word_form` entities, composable via normal syntax.
3. **A `lexicalized_compound` edge** connecting (1) whole-form ←→ (2) constituent parts, role-ordered, with left-to-right ordinal carried on edge_member positions.

### The query pattern

```sql
-- Is this surface form a lexicalized compound?
SELECT lc.id
FROM substrate.edge lc
JOIN substrate.edge_member m_source ON m_source.edge_id = lc.id AND m_source.edge_role_id = :source
WHERE lc.edge_type_id = :lexicalized_compound
  AND m_source.entity_id = :scurvy_dog_lemma_id;

-- If yes, what senses does the WHOLE form carry?
SELECT sense_edge.id, s.mu
FROM substrate.edge sense_edge
JOIN substrate.edge_member m ON m.edge_id = sense_edge.id AND m.edge_role_id = :source
JOIN substrate.significance s ON s.edge_id = sense_edge.id
WHERE sense_edge.edge_type_id = :has_sense
  AND m.entity_id = :scurvy_dog_lemma_id;
```

### The geometric payoff

The whole-form lemma `scurvy_dog` has its own centroid, derived from attested pejorative contexts. The compositional centroid (computed from `centroid(scurvy)` and `centroid(dog)`) points in a different direction — toward disease + canine.

The **Euclidean displacement** between these two centroids is the *idiomaticity measure* — see `specs/native/geometry4d-composition.md` § "Idiomaticity as Euclidean distance". This number is computable on demand, audited by provenance, and does not require a classifier.

The substrate recognizes that "scurvy dog" does not mean "diseased canine" because the whole-form attested centroid is far from the compositional centroid and clusters with other pejorative-human-term centroids.

---

## Anti-patterns

The following are explicitly prohibited:

### Anti-pattern 1: Putting classification rows into `substrate.entity`

> "Let's add a row to `substrate.entity` for each POS tag so we can traverse into them as entities."

This destroys the app/substrate separation. POS tags are classification vocabulary, not content. They belong in `substrate.pos`. If you want POS to be traversable, use `entity_pos` junction rows as edge targets — but POS itself is not content.

### Anti-pattern 2: Storing Glicko ratings on both surfaces for the same assignment

> "Let's rate the `has_sense` edge AND the `entity_sense` junction."

You can do both, but they rate different things: the edge rates *the attested relation's evidentiary strength in the corpora*, and the junction rates *the classification confidence across all evidence*. Keep them distinct. Do not copy ratings between them.

### Anti-pattern 3: Skipping the app layer and doing all classification via traversal

> "Who needs junctions — we can answer 'is this a noun' by traversing `has_pos` edges."

If your query runs millions of times per batch, traversal is the wrong tool. That is what junctions are for. The app layer exists precisely because traversal is the wrong order of complexity for classification lookups.

### Anti-pattern 4: Skipping the substrate and answering content questions from junctions alone

> "We don't need the full edge structure — we can infer meaning from POS junction ratings."

Classifications alone do not answer content questions. The substrate holds the attested content, the geometry, the provenance, and the edges. Without it, you have a vocabulary, not a cognition.

### Anti-pattern 5: Letting app-layer data become irreplaceable

> "Let's write corpus-specific one-off data directly into junction tables without provenance tracking."

The app layer must remain rebuildable from seeds + decomposer runs. If you write junction data without provenance, you break the rebuild contract and the app layer becomes substrate-like (irreplaceable) without being content-addressed. Either put the data in the substrate (as edges with provenance) or route it through a decomposer that can re-emit it.

---

## Cross-references

- `familiar-principle.md` — The conceptual frame that motivates the separation (Corollary 5: Infrastructure is not substrate).
- `architecture.md` — Schema overview.
- `type-system.md` — Complete classification vocabulary enumeration.
- `specs/sql/reference-tables.md` — DDL for all reference tables.
- `specs/sql/junction-tables.md` — DDL for all junction tables.
- `specs/sql/partitioning.md` — Substrate partitioning strategy.
- `specs/engine/arenas-and-significance.md` — Glicko-2 machinery.
- `specs/engine/substrate-governance.md` — How governance uses the app layer as enforcement surface during the forward pass.
- `specs/native/geometry4d-composition.md` — How substrate centroids and compositional geometry work.
