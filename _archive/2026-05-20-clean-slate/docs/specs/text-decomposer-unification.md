# Text Decomposer Unification — Design Spec

**Status**: IMPLEMENTED (Option B / hash-only entity PK chosen and shipped pre-v1)
**Scope**: substrate-foundation refactor — schema, decomposers, inference, re-seed
**Risk**: realised; canonical schema in `sql/schema/` reflects the chosen design
**Audience**: archived for the design rationale; current truth is `sql/schema/tables/core/entity.sql` (hash PK only) and `sql/schema/tables/core/entity_classification.sql` (multi-classification per content)

> **What landed**: Option B. `substrate.entity` is a single non-partitioned table with `hash hash_value PRIMARY KEY`. There is no `entity_type_id` on the entity table. Structural classification of content is recorded in `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`, allowing the same content to carry multiple classifications without fragmenting identity. All edge_member, physicality, entity_significance, and junction tables FK to `substrate.entity(hash)` via the single `entity_hash` column. Sections below are preserved for historical context; ignore any text suggesting `(entity_type_id, hash)` composite PK is current state.

---

## 1. Problem Statement

The substrate's design promises **content-addressed identity**: same content from any source collapses to one entity via a deterministic BLAKE3 Merkle of its constituents. The implementation today violates this in three layered ways:

1. **Multiple text decomposers exist with divergent algorithms.** The same string `"dog"` produces different hashes depending on which code path runs. Empirically verified in this session: the seed-time `dog` word_form (Tatoeba via `TextSegmentationEmitter`) has hash `0ed9dfc3…`; recent prompt-path word_forms via the same emitter produce hashes `2ec3f7e4…`, `f2a95685…`, etc. The "Merkle of grapheme clusters" promise is not deterministic across paths.

2. **The composite primary key `(entity_type_id, hash)` fragments identity.** Even when hashes happen to match (673,891 documented lemma/word_form hash collisions in current data), the substrate stores the same content as TWO rows because of the type-keyed PK. `dog` the word_form and `dog` the lemma are SEPARATE entities. The classifications "word_form" and "lemma" should be junction metadata, not identity.

3. **Multiple text-emit paths emit inconsistent substructure.** `TextSegmentationEmitter` emits entities + significance + (after a recent fix) one layer of sequence rows but no physicality, no word_form trajectory, no grapheme contour, no codepoint S³ POINTZM. `EmitWordFormMerkle` emits the full 4D physicality hierarchy + sequence at every layer + centroid math. Neither alone is correct; both produce different hashes; they are simultaneously deployed. The `.claude/rules/00-hartonomous-core.md` rule "**Seed-uses-core is non-negotiable**" — every text-bearing content goes through the canonical text decomposer — is violated.

The empirical consequence: forward-pass inference on a prompt containing `dog` cannot bridge to seed-emitted knowledge about `dog`, because the prompt's `dog` and the seed's `dog` are different entities with different hashes living in different tables.

## 2. Invention Restatement

(Verbatim from `.claude/rules/00-hartonomous-core.md` and conversational design, condensed:)

- **Same content = same hash = same entity.** The classification (word_form, lemma, codepoint, grapheme_cluster, etc.) is a property OF the entity, not part of its identity. `"dog"` is `"dog"`.
- **The text decomposer records the FULL Merkle DAG**, not just the top hash:
  - codepoint atoms, each with POINTZM at S³ position (UCA Super-Fibonacci projection)
  - grapheme_cluster compositions, with LINESTRINGZM contour through codepoint centroids
  - word_form compositions, with LINESTRINGZM trajectory through grapheme centroids
  - text_composition / paragraph / document compositions, with LINESTRINGZM trajectories up the tree
  - sequence rows at every parent→child layer (RLE-compressed for repeats)
  - centroids memoized once per entity in `substrate.physicality`
  - significance priors per layer per arena
  - per-call provenance routing
- **All text-bearing content from any decomposer routes through the canonical text decomposer.** WordNet glosses/lemmas/synsets, Wiktionary lemmas/etymology/pronunciation/hyphenation/translation/example, OMW foreign lemmas, UD tokens/sentences, Tatoeba sentences, Safetensors model artifacts (config.json/README/tokenizer.json), prompts — one function, one hash, one substructure.
- **Inference uses indexed lookup + A*** over typed edges, with Glicko-2-rated significance per arena. Geometry layer (Fréchet/Hausdorff over LINESTRINGZM trajectories, Voronoi consensus over firefly clouds) participates first-class.

## 3. Current State Audit

Verified empirically this session against the live substrate (post-seed-completion as of 2026-04-30):

### Data presence

| Source | Expected | Actual | % |
|---|---|---|---|
| UD `has_lemma` | ~3M+ for English alone, tens of millions across 339 treebanks | 49,515 | ~1% |
| UD deprel edges (nsubj/obj/punct/case/etc.) | hundreds of millions | ~250K | ~1% |
| Wiktionary `translation_of` | tens of millions | 268,002 | ~3% |
| Wiktionary `synonym` | 5–20 per entry × 1.46M entries | 44,276 | ~3% |
| Tatoeba `translation_link` | 25M scanned per log | 246,788 | ~1% |
| Wiktionary etymology templates | 2–4M total across 8 types | ~280K | ~10% |
| WordNet `has_sense` | 207K from index.sense | 206,978 | ~100% (OK) |
| Tatoeba audio | ~942K English | 851,136 | ~90% (OK) |
| AI model tensors | 101 per filtered model | 0 | 0% |
| `edge_significance` priming | 100M (10M edges × 10 arenas) | 450,816 | 0.45% |

### Algorithm divergence

- `TextSegmentationEmitter.EmitTextComposition` (Hartonomous.Core/Text/Segmentation) and `BaseDecomposer.EmitWordFormMerkle` (Hartonomous.Core/Decomposition) compute different hashes for the same UTF-8 input despite both being "BLAKE3 Merkle over grapheme clusters of the word."
- 673,891 lemma/word_form entity rows share a hash WHEN both go through `EmitWordFormMerkle` / `EmitLemmaMaybeCompound`; cross-decomposer dedup only works among those callers.

### Substructure divergence

| Layer | `TextSegmentationEmitter` | `EmitWordFormMerkle` |
|---|---|---|
| codepoint entity | ✓ | ✓ |
| grapheme_cluster entity | ✓ | ✓ |
| word_form entity | ✓ | ✓ |
| text_composition entity | ✓ | n/a |
| codepoint POINTZM at S³ | **✗** | ✓ |
| grapheme contour LINESTRINGZM | **✗** | ✓ |
| word_form trajectory LINESTRINGZM | **✗** | ✓ |
| text_composition trajectory LINESTRINGZM | **✗** | n/a |
| sequence: grapheme→codepoint | **✗** | ✓ |
| sequence: word_form→grapheme | **✗** | ✓ |
| sequence: composition→word_form | ✓ (recent fix) | n/a |
| RLE for repeats | **✗** | partial |
| significance per arena per layer | source_authority only | source_authority only |
| centroid memoization | **✗** | ✓ |

### Glicko-2 status

- 0.45% of expected edge_significance rows initialized
- 99.55% of edges fall back to static `provenance.initial_mu × edge_type.semantic_weight × provenance.derivation_decay` at every A* expansion
- 0 outcome→update events have ever fired (Step 6 of `inference.md` — the comparison-event Glicko-2 update loop — is unimplemented in code)
- Every existing significance row is at its initial mu; no row's rating has ever evolved from feedback

## 4. Canonical Text Decomposer Contract

A new module `Hartonomous.Core.Text.CanonicalTextDecomposer` becomes the SINGLE authoritative implementation of text → substrate emission. Every decomposer that touches text routes through it. The contract:

### 4.1 Signature

```csharp
public static class CanonicalTextDecomposer
{
    public static TextDecomposeResult Emit(
        IIngestionBatch batch,
        ReadOnlySpan<byte> utf8,
        ICodepointProperties codepointProperties,
        TextDecomposeOptions options);
}

public readonly record struct TextDecomposeOptions(
    string ProvenanceCode,        // user_session, tatoeba, wiktextract, princeton_wordnet, …
    string TopEntityType,         // "text_composition" | "lemma" | "language_name" | …
    double TrustMu,               // arena prior for source_authority
    bool   EmitPhysicality,       // true everywhere except where caller explicitly opts out
    bool   EmitSequence,          // same
    bool   EmitRleCompaction,     // detect runs of identical children, collapse to one row
    bool   MemoizeCentroids);     // write-once centroids to substrate.physicality

public readonly record struct TextDecomposeResult(
    EntityHandle RootHandle,
    byte[] RootHash,
    long EntitiesEmitted,
    long SequenceRowsEmitted,
    long PhysicalityRowsEmitted,
    long SignificanceRowsEmitted,
    (double X, double Y, double Z, double M) RootCentroid);
```

### 4.2 Algorithm (deterministic, byte-for-byte reproducible)

**Stage 0 — UTF-8 decode.** Reject invalid sequences with explicit error (no silent skip). Each codepoint produces a `(rune_value, byte_offset, byte_length)` tuple.

**Stage 1 — Codepoints.**
- Hash: `BLAKE3(big-endian 4-byte rune_value)`
- Entity: `AddEntity(hash, "codepoint")`
- POINTZM physicality: `(x, y, z, m) = UcaSuperFibonacciS3(rune_value)` — deterministic given the codepoint integer; declared seed in the spec
- Significance: `AddSignificance(handle, "source_authority", trustMu)`
- Junction: `codepoint_property` (gcb_id, wb_id, sb_id, lb_id, general_category_id, script_id, block_id) — unchanged from current

**Stage 2 — Grapheme Clusters (UAX #29).**
- Boundary detection via `codepoint_property.gcb_id`, identical to current `EmitWordFormMerkle` path. Method shared.
- Hash: `BLAKE3.Merkle(child codepoint hashes in left-to-right order)`. Single-codepoint cluster: `BLAKE3.Merkle([cp_hash])` = native `blake3_merkle` of one element.
- Entity: `AddEntity(hash, "grapheme_cluster")`
- LINESTRINGZM contour through codepoint centroids (single-codepoint: degenerate to its POINTZM; ≥2 codepoints: real LINESTRINGZM)
- Centroid: `MeanCentroid(constituent_codepoint_centroids)`
- Sequence: `AddSequence(grapheme_cluster, ordinal=i, codepoint_i)` for each codepoint, RLE-compressed
- Significance: `AddSignificance(handle, "source_authority", trustMu)`

**Stage 3 — Word_Forms (UAX #29 word boundaries).**
- Boundary detection via `codepoint_property.wb_id` — UNIFIED with the path `EmitWordFormMerkle` uses (currently `StringInfo.GetTextElementEnumerator` — ALGORITHM CHOICE: switch to UAX #29 via codepoint properties for consistency with other layers).
- Hash: `BLAKE3.Merkle(child grapheme_cluster hashes)`
- Entity: `AddEntity(hash, "word_form")`
- LINESTRINGZM trajectory through grapheme_cluster centroids
- Centroid memoized
- Sequence: word_form → grapheme_cluster, RLE
- Significance per layer per requested arena

**Stage 4 — Composition (sentence / paragraph / document tier).**
- Top entity type from `options.TopEntityType` (caller specifies whether this is a `text_composition`, a `lemma`, a `language_name`, etc.)
- Children are an ordered mix of word_forms and **raw_span** (whitespace/punctuation) compositions; raw_span is recursively a text_composition over its codepoints, ensuring byte-identical round-trip recompose.
- Hash: `BLAKE3.Merkle(child hashes in linear order)`
- LINESTRINGZM trajectory through child centroids
- Centroid memoized
- Sequence: composition → child, RLE
- Significance

**Stage 5 — Provenance routing.**
- Every emit path (entity, edge, junction, significance) carries the caller's provenance code. WordNet glosses ingested via the canonical text decomposer get `provenance='princeton_wordnet'`; Wiktionary etymology gets `wiktextract`; Tatoeba sentences get `tatoeba`; prompts get `user_session`. Same hash, possibly multiple provenance attachments.

### 4.3 Determinism Requirements

- **Same input bytes always produce identical output.** Byte-for-byte equal entity rows, sequence rows, physicality rows, hash sequences. Verified by repeat-decompose tests.
- **Same input bytes from different decomposers produce identical hashes for identical content.** WordNet's `dog`, Wiktionary's `dog`, Tatoeba's `dog`, prompt's `dog` all collapse to one entity row.
- **No PRNG without declared seed.** UCA Super-Fibonacci S³ projection seed declared in the spec.
- **No platform-dependent operations.** `StringInfo.GetTextElementEnumerator` (.NET-native grapheme clustering) replaced with UAX #29 via `codepoint_property.gcb_id` (substrate-table-driven, platform-independent).

### 4.4 Acceptance Test for the Canonical Decomposer

Three deterministic test vectors:

1. **Empty input** → empty Merkle root, single root entity, zero children, zero sequence rows.
2. **Single ASCII word `"dog"`** → produces a known fixed hash (declared in the spec). Test vector locked in `tests/Hartonomous.Core.Tests/Text/CanonicalTextDecomposerVectors.cs`.
3. **Multi-grapheme Devanagari word** (e.g., `"गृह"` — 3 codepoints, 2 grapheme clusters per UAX #29) → known fixed hash declared.

A test that runs the decomposer twice on the same input and asserts byte-equal output (modulo emit order) gates every CI run.

A test that verifies cross-caller equality: emit `"dog"` via the canonical decomposer twice with different provenance codes (one pretending to be Tatoeba, one pretending to be WordNet) and assert the entity hashes are identical.

## 5. Call-Site Inventory

Every place text decomposition currently happens. The unification replaces ALL of these with a single call to `CanonicalTextDecomposer.Emit`.

### 5.1 Functions to delete

- `BaseDecomposer.EmitWordFormMerkle` (`src/Hartonomous.Core/Decomposition/BaseDecomposer.cs:294`)
- `BaseDecomposer.EmitLemmaMaybeCompound` (`src/Hartonomous.Core/Decomposition/BaseDecomposer.cs:541`)
- `BaseDecomposer.EmitLexicalizedCompound` (referenced at `src/Hartonomous.Core/Decomposition/BaseDecomposer.cs:531`; signature TBD via grep)
- `TextSegmentationEmitter.EmitTextComposition` (`src/Hartonomous.Core/Text/Segmentation/TextSegmentationEmitter.cs:41`)
- `TextDecomposer.IngestUtf8DocumentIntoBatch` (`src/Hartonomous.Decomposers/Text/TextDecomposer.cs:119`)
- The `Segment` private function inside `TextDecomposer.cs` (segmentation logic absorbed into canonical decomposer)
- `TextIngestingDecomposer.IngestText` (`src/Hartonomous.Decomposers/TextIngestingDecomposer.cs:61` — wrapper helper added earlier in this work)

### 5.2 Call sites requiring rewrite

#### Hartonomous.Core
- `BaseDecomposer.cs:548, 553, 601, 608` — internal calls within deleted helpers; gone with the helpers

#### Hartonomous.Decomposers
- `Iso639Decomposer.cs:108, 154, 163` — language_name emission. Currently uses `EmitWordFormMerkle(batch, name, "language_name")`. Replace with `CanonicalTextDecomposer.Emit(batch, utf8(name), props, opts(provenance="sil_international", topType="language_name", trustMu=70000))`.
- `OmwDecomposer.cs:182` — foreign lemma emission. Replace with canonical emit, topType="lemma", provenance="omwn_consortium".
- `UdDecomposer.cs:246` — token form emission (`EmitWordFormMerkle`). Replace, topType="word_form", provenance="universaldependencies".
- `UdDecomposer.cs:264` — lemma emission (`EmitWordFormMerkle(...,"lemma")`). Replace, topType="lemma".
- `WiktionaryDecomposer.cs:172, 310, 335, 467` — entry word, etym source, translation target, semantic relation target. All `EmitLemmaMaybeCompound`. Replace, topType="lemma", provenance="wiktextract".
- `WiktionaryDecomposer.cs:197` — inflected form emission. Replace, topType="word_form".
- `WordNetDecomposer.cs:211, 389, 446` — synset member words, verb sense lemmas, morph base form. Replace, provenance="princeton_wordnet".
- `WordNetDecomposer.cs:432` — morph exception inflected forms. Replace, topType="word_form".
- `TatoebaDecomposer.cs:261, 340` — sentence text and audio recording text. Replace, topType="text_composition", provenance="tatoeba".
- `Safetensors/Passes/ModelPassOrchestrator.cs:159, 402` — architecture name and other model-derived strings. Replace, topType="text_composition", provenance="huggingface_model".
- `Safetensors/Passes/ModelTextArtifactsPass.cs:96` — config.json, README, tokenizer.json text artifacts. Replace.

#### Hartonomous.Engine
- `Inference/SubstrateInferenceEngine.cs:77` — prompt ingestion. Replace, topType="text_composition", provenance="user_session", trustMu=1000.

#### Hartonomous.Decomposers (helper class)
- `TextIngestingDecomposer.cs:61` — `IngestText` helper used by WordNet glosses/examples and Wiktionary etymology/pronunciation/hyphenation/translation. Becomes a thin wrapper around `CanonicalTextDecomposer.Emit` with the subclass's provenance and trustMu.

### 5.3 Inline-emission paths to absorb

These do partial text-DAG emission in their own code instead of calling a decomposer. They must be re-routed through the canonical decomposer:

- TBD — needs a deeper grep pass on each decomposer for places that hand-emit codepoint/grapheme/word_form entities. Sub-task in §15.

## 6. Schema Impact Analysis

### 6.1 Decision point: composite PK collapse

**Option A — keep `(entity_type_id, hash)` PK.** The canonical decomposer ensures all callers produce the same hash for the same content; classifications are recorded as separate entity rows (one per type). Cross-type bridging happens in inference via hash-equality lookup across types (current `same_content_other_types` CTE in `substrate.infer`). This is what's deployed today AFTER the canonical decomposer fix; it's the smaller refactor.

**Option B — collapse to `(hash)` PK; classifications via junction.** True content-addressed identity. One entity row regardless of how many decomposers touch it. Classifications via `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)` junction. Smaller storage, simpler dedup, faithful to the invention.

**Recommendation: Option B.** Option A leaves the architectural violation in place (two rows for `dog`, even if same hash). Option B is the user's stated invention. Option B is more invasive (touches every dependent table) but is correct.

The rest of this doc assumes Option B. If we choose A, §7 and §9 simplify; the schema migration is much smaller; §12 (geometry integration) is unaffected.

### 6.2 Tables with `(entity_type_id, hash)` columns under Option B

All of these need to drop the `entity_type_id` column from the entity-reference foreign key:

- `substrate.entity` — PK becomes `(hash)`. Drop LIST partition by `entity_type_id`; switch to hash-range or single-table.
- `substrate.entity_classification` — NEW table.
- `substrate.edge_member` — keys drop `entity_type_id` from member reference. The edge's own `(edge_type_id, hash)` PK can remain (edge_type IS structural).
- `substrate.sequence` — `parent_entity_type_id` and `child_entity_type_id` columns drop.
- `substrate.physicality` — `entity_type_id` column drops; physicality references entity by hash only. Partition strategy reconsidered.
- `substrate.entity_significance` — `entity_type_id` drops. Partition by `context_type_id` only.
- `substrate.entity_pos`, `entity_lexname`, `entity_language`, `entity_morph_feature`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel` — `entity_id` becomes `entity_hash`; type column drops.
- `substrate.staging_*` — every staging table mirrors the substrate shape; all updated.

### 6.3 Functions to update

- All 8 `substrate.drain_staging_*_chunk` functions
- `substrate.recompose_text` (currently takes `(entity_type_id, hash)`)
- `substrate.upsert_model_pass_checkpoint` (unchanged — it doesn't reference entity directly)
- `substrate.prime_unprimed_edges_chunk` (unchanged — operates on edge_type/hash, not entity)
- `substrate.infer` (rewritten — see §9)
- All seed/reference-data functions that look up entities

### 6.4 C extension

- `pg_traverse_astar` — currently signature `(seed_entity_type_id, seed_entity_hash, edge_type_filter, arena_id, ...)`. Either keep entity_type for filtering or drop it. Drop if Option B.
- `pg_neighbors` — same.
- `traversal_path` and `neighbors_result` SQL composite types — drop `target_entity_type_id`.

## 7. Migration Sequence

Ordered by dependency. Each migration up/down pair is its own commit.

| # | Migration | Description |
|---|---|---|
| 0025 | `unified_text_decomposer_classification_junction` | Create `substrate.entity_classification`. Backfill rows from existing `substrate.entity` types: for every existing `(entity_type_id, hash)` row, create matching classification row (default provenance `system_computed` for backfilled). |
| 0026 | `entity_pk_collapse_phase1_drop_secondary` | Drop indexes/constraints that depend on `(entity_type_id, hash)` PK on dependent tables (edge_member, sequence, physicality, entity_significance, junctions). Replace with `(hash)`-only foreign keys. Drop `entity_type_id` columns. |
| 0027 | `substrate_entity_pk_to_hash` | Drop existing `substrate.entity` LIST partition. Recreate as `PRIMARY KEY (hash)`. Re-import data. |
| 0028 | `c_extension_neighbor_signature_v2` | Update `pg_traverse_astar`, `pg_neighbors`, `traversal_path`, `neighbors_result` to drop entity_type from member references. Keep edge_type. Bump extension version to `2.0`. |
| 0029 | `recompose_text_hash_only` | `substrate.recompose_text(entity_hash)` instead of `(entity_type_id, hash)`. |
| 0030 | `infer_function_hash_only` | Rewrite `substrate.infer` against the new schema; simpler seed-activation logic (no cross-type same-content lookup needed because content IS the entity). |
| 0031 | `staging_tables_hash_only` | All `substrate.staging_*` tables updated to new shape. |
| 0032 | `glicko2_priming_full_run` | Reset `substrate.arena_priming_state` and run priming to completion across all arenas. (May be a separate operational step rather than a migration.) |

This must run on a fresh DB (`drop → create → migrate`) — NOT on an existing seeded substrate. Existing substrate's entity rows have hashes that the OLD algorithm produced; new code will produce different hashes. The substrate is incompatible.

## 8. Re-seed Plan

After the schema migrations and decomposer updates are in:

1. **Drop and recreate the database** (the user's normal rebuild flow handles this).
2. **Migrate to head.**
3. **Run all phases in order** under the new code:
   - CoreAlgebra → UcdUca → Iso639 → WordNetOmw → UniversalDeps → Wiktionary → Tatoeba → TextDecomp → ModelDecomp → SignificanceField → InferenceEngine → Validation
4. **Verify hash determinism**: run a small subset of each decomposer twice; assert byte-equal substrate state (down to entity/edge/sequence/physicality row counts and hashes).
5. **Verify cross-decomposer dedup**: assert `dog` exists as exactly one entity row regardless of which decomposers emitted it.
6. **Run `substrate.infer` against known prompts** ("dog", "the cat sat on the mat", "minute", "highrise"); expect non-zero seed counts, non-zero distinct targets, non-empty answers (recomposed terminal entities — synset glosses, etymology text, translations).

## 9. Inference Path Updates

Under Option B (hash-only PK), `substrate.infer` becomes:

```sql
-- Pseudo-spec; details in implementation phase
CREATE FUNCTION substrate.infer(
    p_prompt_hash hash_value,
    p_max_depth   INT DEFAULT 5,
    p_max_results INT DEFAULT 50
) RETURNS TABLE (...) AS $$
BEGIN
    -- Step 1: seeds = the prompt's word_form children + ANY entity sharing
    -- a hash with those word_forms (since hash IS the entity, this is a
    -- straight PK probe — there's no longer a "different type same hash"
    -- bridge needed; one PK = one entity).
    -- Step 2: cross-arena traverse_astar fan-out (C extension call per arena).
    -- Step 3: max-pool path significance per terminal entity.
    -- Step 4: composition assembly (Step 4 of inference.md, separate work).
    -- Step 5: write trace as substrate content.
END $$;
```

Composition assembly (Step 4 — POS-fitness selection + UD-deprel sequencing for fluent prose answer) is its own engineering work beyond this doc; out of scope for the unification refactor, but the substrate the unification produces is the precondition.

## 10. Glicko-2 Priming + Outcome Loop

### 10.1 Priming to 100%

After re-seed, every edge gets per-arena significance row at insertion via inline cross-product (currently the async primer; preferred to be inline at drain time per AP-1 rule). Migration step:

```sql
-- One-shot bulk-prime per arena, single-partition INSERT (verified safe in this
-- session: arena 1 ran 10M rows in one statement without crashing PG):
INSERT INTO substrate.edge_significance ...
SELECT (each arena, each edge, computed mu/sigma) ...
ON CONFLICT DO NOTHING;
```

This converts the 0.45% coverage to 100% in one pass.

### 10.2 Outcome → update loop (Step 6)

For real Glicko-2 ratings to evolve, `substrate.infer` must, on outcome signal:

1. Identify the chosen path's edges (from the trace it wrote in Step 5).
2. Identify the rejected paths' edges (also from the trace; or from pooled candidates rejected in Step 3).
3. Fire `substrate.record_comparison` (already exists per `sql/schema/functions/record_comparison.sql`) for each (winner_edge, loser_edge) pair in the relevant arena.
4. Glicko-2 update: winner mu rises, loser mu falls, sigma tightens with games count.

The C# side needs an outcome-signaling API: after the user/system accepts/rejects/scores an answer, post the outcome to `substrate.record_comparison` against the trace.

This is also out of scope for the unification refactor itself but documented here so the precondition (priming to 100% + trace as substrate content) is clear.

## 11. Decomposer Completeness Fixes

Independent of the unification but documented here because they're necessary for the substrate to actually work:

### 11.1 UD at 1%

Diagnosis pending. Hypotheses:
- `UdDecomposer.cs` may be filtering treebanks too aggressively (LanguageFilter check at line 89)
- May be skipping CoNLL-U lines silently on parse error
- May be failing the FK guard at edge insertion

Sub-task in §15: trace why UD produces 49K has_lemma instead of millions.

### 11.2 Wiktionary translations at 3%

Diagnosis pending. Hypotheses:
- `WiktionaryDecomposer.cs:318-343` translation loop may be filtering by some condition that drops most
- Foreign lemma emission via `EmitLemmaMaybeCompound` may fail for non-English text (encoding, script properties not in cache)

Sub-task: trace why 268K instead of millions.

### 11.3 AI model decomposition

Migrations 0023 (per-role-unit seeds) and 0022 (model_pass_checkpoint NOT NULL fix) restore it; verify by running `--phase ModelDecomp` after re-seed.

## 12. Geometry Layer Integration

The 4D physicality the canonical decomposer emits gets used by inference:

### 12.1 Edge trajectory similarity

Every edge has `geom` populated from participant centroids in role order at insert. Analogy queries:

```sql
SELECT e.* FROM substrate.edge e
WHERE e.edge_type_id = $1  -- e.g., gender_correspondence
ORDER BY substrate.st_4d_frechet_distance(e.geom, $2::query_trajectory) ASC
LIMIT 10;
```

GiST 4D-aware index prunes O(log N).

### 12.2 Voronoi consensus over firefly clouds

For cross-model token agreement: pull all firefly physicality rows for an entity, compute 4D centroid via `substrate.st_4d_centroid` aggregate, compute Voronoi cell. Tight cell = agreement.

### 12.3 Frayed edges as inference signals

`substrate.frayed_edges` (migration 0030) flags geometric anomalies: pairs whose 4D centroids are within Fréchet threshold of an edge type's archetype but no edge exists. Inference encountering a frayed edge in traversal records it as substrate content (curiosity-driven exploration trigger).

These all exist in substrate functions already; inference engine needs to use them.

## 13. Test Plan and Acceptance Gates

| Gate | What passes | Verification |
|---|---|---|
| G1: canonical decomposer determinism | Same input → byte-equal substrate state on repeat decompose | xUnit test in `tests/Hartonomous.Core.Tests/Text/CanonicalTextDecomposerTests.cs` |
| G2: cross-caller hash equality | `dog` emitted via every wrapper produces identical entity hash | Same test file |
| G3: substructure completeness | Each input produces full DAG: codepoint+grapheme+word_form+composition entities, sequence rows at every layer, physicality at every layer, RLE applied, centroids memoized | Same test file with row-count assertions |
| G4: schema migration 0025-0031 round-trip | up + down + up produces clean state | `tests/Hartonomous.Integration.Tests/Migrations/UnificationRoundTripTests.cs` |
| G5: cross-decomposer dedup post-re-seed | `SELECT count(*) FROM substrate.entity WHERE hash = blake3(`dog`-canonical)` returns exactly 1 | SQL probe in integration test |
| G6: inference produces non-empty answer | `query "dog"` returns a recomposed terminal (synset gloss, etymology text, etc.) | CLI integration test |
| G7: UD ingestion delta | After fix in §11.1, has_lemma count >= some threshold (TBD by treebank size) | SQL probe |
| G8: Wiktionary translations delta | After fix in §11.2, translation_of count >= some threshold | SQL probe |
| G9: edge_significance 100% coverage | After §10.1 bulk prime, count = total_edges × total_arenas | SQL probe |

Each gate has a specific SQL or test assertion, not a vibe check.

## 14. Risk Register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| PG18 SEGV under bulk INSERT during re-seed | High (documented) | Re-seed fails | Use single-partition bulk inserts where possible; fall back to chunked when crossing partitions |
| Hash determinism violated by platform-dependent grapheme-cluster algorithm | Medium | Cross-platform substrate divergence | Switch to UAX #29 codepoint-property-driven grapheme clustering, no `StringInfo.GetTextElementEnumerator` |
| Re-seed time prohibitive (Wiktionary 1.46M entries × full DAG emit ≈ hours) | Medium | Demo delayed | Streaming pipeline tuned, text cache (already added) deduplicates; expect 1-3 hours per phase |
| Existing inference engine depends on type-keyed lookup; refactor breaks tests | High | Test failures | Tests are part of the deliverable; updated alongside the migration |
| Composition assembly (Step 4) still missing after unification | Certain | Inference returns terminal recompose, not synthesized prose | Documented as out of scope for THIS refactor; separate engineering work |
| Glicko-2 outcome loop wiring is its own bug surface | Medium | Substrate doesn't actually learn yet | Documented as separate work in §10.2 |
| User redirects scope before doc is reviewed | Possible | Wasted work | Doc is reviewable in chunks; sections 1-4 are foundation |

## 15. Task Decomposition

Ordered by dependency. Each task has an explicit gate (§13).

### Phase A — Spec lockdown (hours, no code)

A1. Review this doc with user; redirect / approve / reject sections.  
A2. Decide Option A vs Option B (§6.1).  
A3. Lock canonical decomposer test vectors (specific hashes for `""`, `"dog"`, `"गृह"`).  
A4. Final scope sign-off.

### Phase B — Canonical decomposer implementation (1-2 sessions)

B1. Implement `Hartonomous.Core.Text.CanonicalTextDecomposer.Emit` per §4.  
   Gate: G1, G2, G3 pass.  
B2. Inline UAX #29 word-boundary detection via `codepoint_property.wb_id` (replacing `StringInfo`).  
   Gate: G1 still passes; cross-platform.  
B3. Implement `TextDecomposeResult` / `TextDecomposeOptions`.  
B4. Migrate all call sites in §5.2 to use `CanonicalTextDecomposer.Emit`.  
   Gate: solution builds, all decomposer tests pass.  
B5. Delete `EmitWordFormMerkle`, `EmitLemmaMaybeCompound`, `EmitTextComposition` (old), `IngestUtf8DocumentIntoBatch`, `Segment` (private in TextDecomposer), `TextIngestingDecomposer.IngestText` (old wrapper).  
   Gate: build clean.

### Phase C — Schema refactor (2-3 sessions, Option B)

C1. Migration 0025: `entity_classification` junction table.  
C2. Migration 0026: drop `entity_type_id` columns from edge_member, sequence, physicality, entity_significance, all junctions.  
C3. Migration 0027: substrate.entity PK to `(hash)`-only.  
C4. Migration 0028: C extension v2 — `pg_traverse_astar` and `pg_neighbors` signature update.  
C5. Migration 0029: `recompose_text(hash)` signature.  
C6. Migration 0030: rewrite `substrate.infer` for hash-only schema.  
C7. Migration 0031: staging tables shape.  
   Gate per migration: up/down round-trip clean (G4).

### Phase D — Re-seed (operational, 8-12 hours)

D1. Drop/create/migrate.  
D2. Run all phases.  
   Gate: G5 (cross-decomposer dedup verified), all decomposer logs report expected counts.  
D3. Bulk-prime edge_significance.  
   Gate: G9.

### Phase E — Inference verification (1 session)

E1. Run `query` on probe set ("dog", "minute", "highrise", "the cat sat on the mat", semantic eval cases from `.claude/skills/hartonomous-semantic-eval/cases.md`).  
   Gate: G6 passes.  
E2. Smoke-test trajectory similarity, Voronoi consensus, frayed-edge detection.

### Phase F — Decomposer completeness (independent, can parallelize)

F1. UD: diagnose 1% coverage, fix, verify count rises.  
   Gate: G7.  
F2. Wiktionary translations: diagnose 3%, fix, verify count rises.  
   Gate: G8.  
F3. ModelDecomp: verify migration 0023 + 0022 fixes plus phase order let safetensors complete.  
   Gate: model decomp phase reports >0 entities for each pass.

### Phase G — Glicko-2 outcome loop (separate engineering, 1-2 sessions)

G1. C# API for outcome signal post-inference.  
G2. SQL function or PL/pgSQL wrapping `substrate.record_comparison` against the trace.  
G3. Tests demonstrating mu drift after sequence of comparison events.

### Phase H — Composition assembly (separate engineering, multi-session)

Out of scope for this doc; documented here so dependencies are clear. Real engineering work for fluent answer prose. Substrate the unification produces is the precondition.

## 16. Open Questions

Q1. Does the substrate use UAX #29 grapheme-cluster boundaries via `codepoint_property.gcb_id` consistently, or does any path still use `StringInfo`? (Need full grep pass.)

Q2. Is `EmitLexicalizedCompound` (referenced in `BaseDecomposer.cs:531`) used? Need its signature and call sites.

Q3. For Option A vs B: does the user want hash-only PK or composite PK with disciplined classification? (Decision required before C1.)

Q4. For multi-modal prompts (image/audio/video children under a prompt root), does the canonical decomposer dispatch by content type? Or is it text-specific and other modalities have their own canonical decomposers? Recommendation: text-specific name (`CanonicalTextDecomposer`); parallel `CanonicalImageDecomposer`, `CanonicalAudioDecomposer`, etc.; a top-level `Composition` node aggregates children of any modality. Out of scope for THIS doc; flag for future.

Q5. Does the existing `substrate.frayed_edges` machinery work post-schema-refactor, or does it depend on `(entity_type_id, hash)` references that drop?

## 17. Document Status

This is **Draft 0.1**. Items requiring user review before any code change:

- §6.1 PK option choice
- §7 migration sequencing (specifically: is it acceptable to break existing seed data, or must there be an in-place migration path?)
- §11 decomposer completeness fixes — diagnosis sub-tasks not yet done
- §15 task decomposition — dependencies and time estimates
- §16 open questions

After review, this becomes Draft 0.2 with corrections, and Phase A4 sign-off gates code work.
