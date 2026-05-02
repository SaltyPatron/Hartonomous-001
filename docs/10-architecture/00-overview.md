# Architecture — The Three Pillars

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers and architects implementing or extending the substrate.

---

## The substrate stands on three pillars

Every load-bearing technical decision in Hartonomous derives from one of three pillars. If a feature can't be expressed in terms of these three, it doesn't belong in the substrate; it belongs at the boundary (decomposer or recomposer).

```
┌───────────────────────────────────────────────────────────────┐
│                       Hartonomous Substrate                    │
│                                                                │
│   ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐    │
│   │   IDENTITY   │  │   GEOMETRY   │  │  SIGNIFICANCE    │    │
│   │              │  │              │  │                  │    │
│   │  BLAKE3 of   │  │  4D points   │  │  Glicko-2 per    │    │
│   │  content     │  │  & line-     │  │  arena per edge  │    │
│   │              │  │  strings     │  │  & per entity    │    │
│   │  Merkle DAG  │  │              │  │                  │    │
│   │              │  │  S³ atoms,   │  │  Trust priors    │    │
│   │  Convergence │  │  composition │  │  from provenance │    │
│   │  by hash     │  │  trajectories│  │                  │    │
│   │              │  │              │  │  Outcome events  │    │
│   │              │  │  Edge        │  │  → μ updates     │    │
│   │              │  │  fingerprints│  │                  │    │
│   └──────────────┘  └──────────────┘  └──────────────────┘    │
│                                                                │
│   ┌────────────────────────────────────────────────────────┐  │
│   │              Native Compute Extension (C)              │  │
│   │   BLAKE3 SIMD, 4D-aware ops, A* with bulk-fetch SPI,   │  │
│   │   Glicko-2 update, GiST opclasses, Laplacian eigenmap  │  │
│   └────────────────────────────────────────────────────────┘  │
│                                                                │
│   ┌────────────────────────────────────────────────────────┐  │
│   │              Cognitive SQL Surface                     │  │
│   │   ~30+ functions: converse, translate, distill,         │  │
│   │   refine, idiomaticity, frayed_edges, cross_model_*,   │  │
│   │   analogy, recompose_to_safetensors, ...               │  │
│   └────────────────────────────────────────────────────────┘  │
│                                                                │
└───────────────────────────────────────────────────────────────┘
                ▲                                ▼
                │                                │
        ┌───────┴────────┐              ┌────────┴────────┐
        │  DECOMPOSERS   │              │   RECOMPOSERS   │
        │                │              │                 │
        │  Bytes → AST   │              │  Substrate →    │
        │  → substrate   │              │  bytes (per     │
        │                │              │  output format) │
        └────────────────┘              └─────────────────┘
```

## Pillar A — Identity by content

Three kinds of rows have content-addressed identity via BLAKE3:

```
atom_hash         = BLAKE3(canonical_content_bytes)
composition_hash  = BLAKE3(child_hashes_in_canonical_order)        // Merkle
edge_hash         = BLAKE3(edge_type_id || participant_hashes_in_role_order)
```

**Atoms** are leaf entities. The base case is the Unicode codepoint atom: `atom_id = BLAKE3(le32(codepoint_value))`. Other modalities may admit additional atom types (pixel-value atoms, audio-sample atoms, tensor-element atoms) with their own canonical content encoding, but text content always bottoms at codepoint atoms via UAX #29 segmentation.

**Compositions** are recursive Merkle DAG nodes whose hash is over their ordered children's hashes. `walker = [walk, er]` — a 2-vertex composition whose hash is `BLAKE3(H_walk || H_er)`. `walk` is one row everywhere it appears; `er` is one row everywhere it appears; `walker` is one row regardless of which source attested it. Every parent composition shares its children's stored physicality by reference — no duplication.

**Edges** are typed n-ary relations whose hash is over the edge type plus role-ordered participant hashes. `has_sense(walker_form, agentive_synset)` has identity `BLAKE3(has_sense_id || H_walker_form || H_agentive_synset)`. **Edge type IS part of edge identity.** Different edge types between the same two entities produce different edge rows (which is correct: `hypernym(cat, mammal)` and `embedding_similarity(cat, mammal)` are distinct attestations of the same underlying conceptual relationship).

**Convergence is the learning event.** When WordNet attests `hypernym(whale, mammal)` and a corpus generates `co_occurrence(whale, mammal)` and Llama emits `embedding_similarity(whale, mammal)`, three different edges land in the substrate (different types). All three reference the same `whale` and `mammal` entity rows (because content addressing). The substrate now has three independent attestations of the same underlying relationship, queryable across edge types.

**Placement metadata never enters identity.** Filename, source offset, tensor name, ordinal position, line number, timestamp — these are properties of edges (`has_source`, `in_model`) or provenance, never of the hash. Same content in two places = one entity with two edges, never two entities.

Full detail: `10-architecture/02-identity-and-convergence.md`.

## Pillar B — Geometry by trajectory

Every entity has 4D physicality. Atoms are points; compositions are linestrings; edges are linestrings.

| Entity level | Stored type | Construction |
|---|---|---|
| Codepoint atom | `point4d` on S³ | UCA Super-Fibonacci spiral position |
| Embedding-firefly atom | `point4d` in R⁴ | Laplacian eigenmap + Gram-Schmidt + L2 norm projection |
| Grapheme cluster | `linestring4d` | Ordered NFC-canonical codepoint S³ positions |
| Word form | `linestring4d` | Ordered grapheme cluster centroids |
| Lemma / morpheme | `linestring4d` | Ordered word-form / morpheme centroids |
| Sentence | `linestring4d` | Ordered word-form centroids |
| Paragraph | `linestring4d` | Ordered sentence centroids |
| Document | `linestring4d` or `multilinestring4d` | Ordered paragraph centroids; multi for branched docs |
| Edge | `linestring4d` | Ordered participant centroids in role order |

**The trajectory IS the structure.** Order is vertex order in the linestring. Adjacency between two children of a parent is "their centroids are consecutive vertices of the parent's linestring." There are no separate `precedes`/`contains`/`co_occurrence` edges between siblings — those are queries against the geometry, not stored substrate content.

**Recursive memoization.** A parent's linestring vertices are CHILD CENTROIDS, not full child trajectories. A sentence has one vertex per word-form (each vertex = the word's stored centroid), not one vertex per character. Word `the` is one entity with one stored centroid, referenced by billions of sentences with no duplication.

**Operators are 4D-aware.** PostGIS `ST_Distance`, `ST_FrechetDistance`, `ST_Centroid` silently drop the M axis. Substrate uses `substrate.st_4d_distance`, `substrate.st_4d_frechet_distance`, `substrate.st_4d_centroid`, `substrate.st_4d_hausdorff_distance`, `substrate.st_s3_distance`, `substrate.st_s3_centroid` from the native extension. Naive PostGIS use on substrate physicality produces wrong answers.

**The geometry enables the anomaly detector family.** Idiomaticity is `st_4d_distance(centroid_compositional, centroid_lexicalized)`. Frayed edges are pairs whose 4D positions match a relation type's archetype trajectory but no edge exists. Cross-model divergence is Hausdorff over per-model firefly clouds. Suffix similarity (king/sing/ring) is Fréchet over trailing trajectory segments. These aren't separate features; they're queries over the same primitives.

Full detail: `10-architecture/03-geometry-4d.md`.

## Pillar C — Significance by Glicko-2 in arenas

Every edge has Glicko-2 ratings per arena. Same for entities and Glicko-bearing junction tables (`entity_pos`, `entity_sense`, `pattern_deprel`).

```sql
substrate.edge_significance (
    context_type_id INT,    -- arena: lexical_disambiguation, translation_quality, etc.
    edge_type_id INT,
    edge_hash bytea,
    mu FLOAT8 DEFAULT 1500.0,
    sigma FLOAT8 DEFAULT 350.0,
    volatility FLOAT8 DEFAULT 0.06,
    games INT DEFAULT 0,
    PRIMARY KEY (context_type_id, edge_type_id, edge_hash)
) PARTITION BY LIST (context_type_id);
```

**Arenas are open-vocabulary.** Initial set covers ~10 broad domains (`lexical_disambiguation`, `syntactic_role_fitness`, `translation_quality`, `model_trust`, `source_authority`, `semantic_relevance`, `corroboration_strength`, `frequency_significance`, `attention_pattern_confidence`, `morphological_productivity`). New arenas added at runtime auto-backfill into existing edges via a substrate function. **Arena cherry-picking** (hardcoding the 10 starter arenas anywhere in code) is forbidden.

**Trust priors at insert** come from `provenance.initial_mu`. Authoritative sources (UCD = 2000, ISO 639 = 2000, WordNet = 1800, UD = 1600) seed higher than community sources (Wiktionary = 1400, Tatoeba = 1200) and user content (1000). Model-derived edges vary by model reputation.

**Lazy materialization.** With open-vocabulary arenas × billions of edges, eagerly priming all (arena × edge) pairs is impractical. Edges live without explicit `edge_significance` rows; queries `COALESCE(s.mu, p.initial_mu)` from the edge's provenance default. Rows materialize on first traversal touch in a given arena.

**Outcome events drive learning.** When inference produces an outcome (user accept/reject, downstream task succeed/fail, measurable utility), comparison events between selected and rejected paths fire Glicko-2 updates per `(entity, arena)` and `(edge, arena)`. Winners' μ rises, σ falls. Losers' μ falls, σ rises. **Closed-loop learning without gradient descent or labeled data.** The substrate learns from every interaction.

Full detail: `10-architecture/04-significance-glicko.md`.

## How the pillars compose

Identity gives the substrate its rows. Geometry gives the rows positions. Significance gives the edges weights. Together they define a content-addressed graph where:

- Same content from any source = same row (identity)
- Spatial similarity is real and exact (geometry)
- Edge weights reflect cross-source consensus per domain (significance)

Every "AI operation" is a query over this triple-pillar foundation:

- **Inference:** A\* over edges with cost = 1/μ in the requested arena, returning paths whose vertices are entities and whose explanations are provenance traces.
- **Translation:** A\* in `translation_quality` arena from source-language entities to target-language entities along cross-lingual edges.
- **Generation:** Walk substrate state from prompt entities; assemble new compositions per UD deprel patterns weighted by `syntactic_role_fitness`; output bytes via recomposer.
- **Refinement:** Read source model's architecture; populate it from substrate edges with the source's sub-provenance; below-threshold = zero; serialize as safetensors.
- **Idiomaticity:** Geometry primitive — distance between compositional centroid and lexicalized centroid for compounds.
- **Cross-model comparison:** Geometry primitive — Hausdorff over firefly clouds for entities common to multiple ingested models.
- **Analogy completion:** Geometry primitive — Fréchet match against a query trajectory across edges of a given type.
- **Frayed-edge detection:** Geometry primitive — pairs whose 4D positions match an edge type's archetype but no edge exists.
- **Distillation:** WHERE clause selecting edges by significance threshold, recomposer projects to target architecture.

All five operations use the same primitives. None of them require an additional engine.

## What the pillars exclude

If a feature requires anything outside these three pillars, it isn't substrate-native and belongs at the boundary:

- **Approximate nearest neighbor (HNSW, LSH, random projection):** Excluded. Sparsity comes from significance threshold, not approximation. Geometry uses exact 4D operators.
- **Quantization:** Excluded. Tensor decoding is lossless (BF16 → F32 → F64 as needed for internal precision).
- **Stochastic/sampling-based inference at the substrate layer:** Excluded. Inference is deterministic A\*; randomness can be added by recomposers if a particular consumer needs it (e.g., a generation recomposer might sample from a top-k path set).
- **Application-layer significance updates:** Excluded. Glicko updates fire from outcome events through the substrate's update path; app-layer code must not bypass.
- **Inline SQL in app code:** Excluded. App layer calls SQL functions by name; never constructs SQL.

## Concurrency and determinism

- **PostgreSQL MVCC** handles concurrent readers and writers natively. Concurrent ingest of the same content from multiple sources is safe via `ON CONFLICT (entity_type_id, hash) DO NOTHING` on the entity table.
- **Determinism.** Same input + same decomposer version + same substrate state = byte-identical substrate state after ingestion (Substrate Law #6). Exports are similarly deterministic — same substrate state + same recomposer specification = byte-identical safetensors output.
- **Concurrent inference.** Readers see consistent MVCC snapshots. Significance updates from concurrent sessions don't conflict at the row level (different (arena, edge) pairs touch different rows).

## Cross-references

- Substrate laws (the non-negotiable invariants): `10-architecture/01-substrate-laws.md`
- Identity in detail: `10-architecture/02-identity-and-convergence.md`
- Geometry in detail: `10-architecture/03-geometry-4d.md`
- Significance in detail: `10-architecture/04-significance-glicko.md`
- Inference engine and per-hop filtering: `10-architecture/07-inference-engine.md`
- Decomposer contract: `10-architecture/05-decomposer-contract.md`
- Recomposer contract: `10-architecture/06-recomposer-contract.md`
- Cognitive SQL surface: `10-architecture/08-cognitive-surface.md`
- Schema: `20-technical/00-schema-reference.md`
- Anti-patterns from observed agent failures: `40-process/01-anti-patterns.md`
