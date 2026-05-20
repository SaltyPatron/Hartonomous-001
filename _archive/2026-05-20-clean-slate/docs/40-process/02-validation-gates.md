# Validation Gates

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers writing tests, operators verifying substrate health, QA before any release.

---

A validation gate is a falsifiable check that the substrate's accumulated state matches the architectural laws it claims to embody. Each gate is a SQL assertion, a test command, or a benchmark with a documented pass criterion.

Gates are organized by phase: foundational gates that must pass before ANY substrate operation, structural gates that must pass for each pillar, ingestion gates per decomposer, recomposer gates per recomposer, and product-readiness gates per commercial deliverable.

---

## Foundational gates (P0 — must pass before substrate is usable)

### F1 — Schema applies cleanly

```bash
# from a clean PostgreSQL 18 instance with PostGIS 3.6+ and hartonomous_pg installed:
psql -c "CREATE DATABASE hartonomous;"
psql -d hartonomous -f schema/migrations/0001_bootstrap.up.sql
# ... apply all migrations in order
psql -d hartonomous -c "SELECT count(*) FROM ref.entity_type;"
# Expected: 25+ rows (codepoint, grapheme_cluster, word_form, lemma, synset, ...)
```

**Pass:** All migrations apply without error. Reference tables are populated per their seeds.

### F2 — Native extension loads and exposes types

```sql
SELECT extname, extversion FROM pg_extension WHERE extname IN ('hartonomous_pg', 'postgis');
-- Expected: both extensions present, hartonomous_pg version >= 1.0

SELECT typname FROM pg_type WHERE typname IN ('point4d', 'linestring4d', 'box4d');
-- Expected: all three present
```

### F3 — BLAKE3 produces correct output

```sql
SELECT hartonomous.blake3(decode('', 'hex'));
-- Expected: 0xaf1349b9f5f9a1a6a0404dea36dcc949...
-- (BLAKE3 of empty input — first 16 bytes for BLAKE3-128, first 32 for full)

SELECT hartonomous.atom_id(0x6B);  -- codepoint U+006B ('k')
-- Expected: BLAKE3(0x6b 0x00 0x00 0x00) — first 16 or 32 bytes
```

Tests verify against BLAKE3 official test vectors.

### F4 — 4D operators are 4D-aware

```sql
WITH a AS (SELECT hartonomous.make_point4d(0, 0, 0, 0) AS p),
     b AS (SELECT hartonomous.make_point4d(0, 0, 0, 1) AS p)
SELECT
    hartonomous.st_4d_distance(a.p, b.p) AS d4,
    ST_Distance(ST_MakePoint(0,0,0,0), ST_MakePoint(0,0,0,1)) AS d_postgis
FROM a, b;
-- Expected: d4 = 1.0 (4D operator sees the M-axis difference)
--           d_postgis = 0.0 (PostGIS drops M, demonstrating the bug it would introduce)
```

### F5 — A\* traversal smoke test

Hand-construct a 10-node graph; verify A\* returns the optimal path.

```sql
-- Insert 10 entities and edges with known significance
-- Run hartonomous.traverse_astar with seed = entity 0, target_type = entity at end of path
-- Verify the returned path matches the known shortest by edge cost
```

---

## Structural gates (P1 — must pass for each substrate pillar)

### S1 — Identity convergence

```sql
-- Insert "café" twice from different sources (different provenance)
SELECT hartonomous.text_decompose(convert_to('café', 'UTF8'), 1);  -- as if from corpus A
SELECT hartonomous.text_decompose(convert_to('café', 'UTF8'), 2);  -- as if from corpus B

-- Both should produce the SAME text_composition entity hash
SELECT count(DISTINCT hash) FROM substrate.entity
  WHERE entity_type_id = (SELECT id FROM ref.entity_type WHERE code='text_composition');
-- Expected: 1 (one row for "café" regardless of provenance)
```

### S2 — NFC equivalence is an explicit edge, not collapsed

```sql
-- Insert NFC form: U+00E9 (é precomposed) and NFD form: U+0065 + U+0301 (e + combining acute)
-- Both produce the codepoint atoms they decompose to. Their canonical-decomposition relationship
-- is recorded as an edge from UCD seed.

SELECT count(*) FROM substrate.entity WHERE entity_type_id = codepoint_type;
-- Expected: > 0; specifically should include U+00E9 and U+0065 and U+0301 as separate atoms

SELECT count(*) FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
 WHERE et.code = 'canonical_decomposition_of';
-- Expected: > 0 (UCD seeded these)
```

### S3 — 4D physicality is consistent

```sql
-- For every entity that has physicality, the populated coordinate column matches the type's declared shape
SELECT pt.code, count(*)
  FROM substrate.physicality p
  JOIN ref.physicality_type pt ON pt.id = p.physicality_type_id
 WHERE NOT (
    (pt.coordinate_shape = 'point' AND p.point4d IS NOT NULL AND p.geom IS NULL AND p.linestring4d IS NULL) OR
    (pt.coordinate_shape = 'trajectory' AND p.linestring4d IS NOT NULL AND p.geom IS NULL AND p.point4d IS NULL) OR
    (pt.coordinate_shape = 'multi_trajectory' AND p.multilinestring4d IS NOT NULL AND p.geom IS NULL AND p.point4d IS NULL)
 )
 GROUP BY pt.code;
-- Expected: empty result (no constraint violations)
```

### S4 — No orphan compositions

```sql
-- For every text_composition, verify it has the required edges to its constituent word_forms
SELECT e.hash AS orphan_hash
  FROM substrate.entity e
 WHERE e.entity_type_id = (SELECT id FROM ref.entity_type WHERE code='text_composition')
   AND NOT EXISTS (
      SELECT 1 FROM substrate.edge_member em
       WHERE em.entity_type_id = e.entity_type_id AND em.entity_hash = e.hash
   )
 LIMIT 100;
-- Expected: empty (no orphans)
```

### S5 — Significance arena set is correct

```sql
SELECT count(*) FROM ref.significance_context;
-- Expected: 10 starter arenas after seed (lexical_disambiguation, syntactic_role_fitness,
-- translation_quality, model_trust, source_authority, semantic_relevance,
-- corroboration_strength, frequency_significance, attention_pattern_confidence,
-- morphological_productivity)

-- Verify lazy materialization is working: many edges should NOT have edge_significance rows yet
SELECT count(*) AS edges, count(DISTINCT (edge_type_id, hash)) AS distinct_edges
  FROM substrate.edge;
SELECT count(*) FROM substrate.edge_significance;
-- Expected: edge_significance count <= edges × arenas (often much less due to lazy materialization)
```

---

## Decomposer gates (per decomposer)

Each decomposer must pass these before being considered production-ready.

### D1 — Determinism

```bash
# Run decomposer on input X. Capture substrate state hash.
# Truncate substrate. Run on input X again. Capture substrate state hash.
# Assert hashes are identical.
```

### D2 — Idempotency

```bash
# Run decomposer on input X into substrate. Capture state hash S1.
# Run decomposer on input X again (without truncating). Capture state hash S2.
# Assert S1 == S2 (no duplicates added; significance unchanged).
```

### D3 — Convergence cross-source

```bash
# Run decomposer A on input X (which contains some content C).
# Run decomposer B on a different input Y (which also contains content C, byte-identical).
# Verify content C lands at the same entity hash in both runs.
```

For decomposers that should produce overlapping content with prior decomposers (e.g., Tatoeba and WordNet glosses), this gate verifies the seed-uses-core principle.

### D4 — Seed-uses-core compliance

```bash
grep -r 'Blake3.Hash\|blake3_hash\|hartonomous.blake3' src/Hartonomous.Decomposers/
# For text-bearing strings, expect zero direct hash calls.
# Direct hashing of int32 codepoints (atom_id) is fine.
# Direct hashing of role-ordered participant arrays (edge_id) is fine.
# Direct hashing of arbitrary text is forbidden — should go through DecomposeText.
```

### D5 — Fail-loud on bad input

```bash
# Inject a deliberately broken file (truncated, invalid UTF-8, schema violation).
# Run decomposer.
# Verify it halts with diagnostic error.
# Verify substrate state is unchanged from before the run.
```

### D6 — Provenance correctness

```sql
-- After decomposer runs with provenance P, all emitted records carry provenance P
SELECT count(*) FROM substrate.entity WHERE provenance_id != $P;
-- (after subtracting prior runs' contributions)
-- Expected: 0
```

---

## Recomposer gates (per recomposer)

### R1 — Determinism

```bash
# Recompose with spec S. Capture output bytes hash.
# Recompose again with spec S. Capture output bytes hash.
# Assert identical.
```

### R2 — Loadability (for safetensors recomposer)

```python
from transformers import AutoModelForCausalLM
model = AutoModelForCausalLM.from_pretrained("/path/to/recomposed_output")
# Expected: loads without error
```

### R3 — Sample-prompt sanity (for inference-related recomposers)

```python
# Load recomposed model
# Generate output for representative prompt set
# Assert no NaN, no infinity, no crash
# Assert generated text is structurally valid (not gibberish bytes)
```

### R4 — Architecture preservation (for refinement mode)

```bash
# Recompose with target arch matching ingested model
# Diff config.json against original
# Expected: byte-identical (modulo permitted hartonomous_* metadata additions)
```

### R5 — Sparsity threshold honored

```sql
-- Output's nonzero positions count matches expected from substrate state
-- (substrate edges above threshold + required defaults like layer norm)
-- Counts based on substrate query against expected source provenance
```

### R6 — Provenance chain in output metadata

```python
# Open recomposed safetensors
# Read __metadata__ key
# Verify hartonomous_substrate_state, hartonomous_recipe_id, hartonomous_provenance_chain present
# Verify substrate state hash matches the substrate's current state
```

---

## Cognitive function gates (per SQL function)

### C1 — Returns expected type

```sql
SELECT pg_typeof(hartonomous.inference.converse('test'));
-- Expected: TABLE (response_text TEXT, ...)
```

### C2 — Handles edge cases

```sql
SELECT * FROM hartonomous.inference.converse('');
-- Expected: empty result with no error
```

### C3 — Honors arena recipe

```sql
SELECT * FROM hartonomous.inference.converse('test', '{"version":1,"per_hop_overrides":[{"hop":1,"arenas":["lexical_disambiguation"]}]}');
-- Expected: explanation_trace shows arenas_consulted contains lexical_disambiguation
```

### C4 — Performance

```sql
\timing on
SELECT * FROM hartonomous.inference.converse('What is a cat?');
-- Expected: <100ms cold cache; <10ms warm cache
```

---

## Product-readiness gates (per commercial deliverable)

### P1 — Refinement-as-service: round-trip improvement

```bash
# Ingest a known model M (e.g., Qwen2.5-Coder-3B).
# Recompose to refined model M'.
# Run benchmark suite (HumanEval for coders, MMLU for general, etc.) on both M and M'.
# Assert M' >= M on relevant benchmarks for the model's domain.
# Assert |M'| < |M| after sparse-tensor compression.
```

### P2 — Laplace original: end-to-end production

```bash
# Specify Laplace-Linguistics-7B architecture.
# Recompose from substrate state.
# Assert output safetensors loads with HuggingFace transformers.
# Assert sample-prompt forward pass produces coherent output.
# Assert tokenizer.json valid and matches substrate tokenizer state.
```

### P3 — Inference-as-service: SLA

```bash
# Run 1000 representative inference queries via cognitive surface.
# Assert p50 latency < 50ms warm cache.
# Assert p99 latency < 500ms warm cache.
# Assert all responses include valid explanation_trace.
```

### P4 — Cross-model query: coherence

```sql
SELECT * FROM hartonomous.compare.cross_model_consensus('cat');
-- Assert n_models >= 2 (multiple models ingested)
-- Assert centroid is a valid 4D point
-- Assert agreement_score is in [0, 1]
```

### P5 — Audit trail completeness

```sql
SELECT hartonomous.provenance.audit_chain('<some response_entity_id>', 5);
-- Expected: full JSONB chain reaching authoritative source provenance
-- Every edge in the chain has a valid provenance_id
-- Every entity in the chain has at least one provenance row referenced
```

---

## Continuous validation in CI

The CI pipeline runs:

1. **F1–F5** on every push (foundational; cheap)
2. **S1–S5** on every push to main (structural; medium)
3. **D1–D6 per decomposer** when decomposer source changes
4. **R1–R6 per recomposer** when recomposer source changes
5. **C1–C4 per cognitive function** when function source changes
6. **P1–P5** on release branches (full product readiness)

Failure of any P-gate blocks release.

## Cross-references

- The Substrate Laws each gate enforces: `10-architecture/01-substrate-laws.md`
- The decomposer contract enforced by D-gates: `10-architecture/05-decomposer-contract.md`
- The recomposer contract enforced by R-gates: `10-architecture/06-recomposer-contract.md`
- The cognitive surface tested by C-gates: `10-architecture/08-cognitive-surface.md`
- Per-component checklists: `40-process/checklists/`
