# Identity and Convergence

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers implementing decomposers, recomposers, or any code that touches substrate hashes.

---

## The identity primitives

The substrate uses BLAKE3 with full 256-bit output (or BLAKE3-128 truncation where storage matters and collision risk is acceptable; see `Hashing precision` below). Three identity functions:

```
atom_id(content_bytes) :=
    BLAKE3(canonical_content_bytes)

composition_id(child_hashes_in_canonical_order) :=
    BLAKE3(concat(child_hashes))

edge_id(edge_type_id, role_ordered_participant_hashes) :=
    BLAKE3(le32(edge_type_id) || concat(participant_hashes))
```

These three functions are implemented in the native compute extension (`hartonomous_pg`) as C functions exposed via SQL. They are also available via P/Invoke from any application-layer language.

## Atom identity

An atom is a leaf entity. The base case is the Unicode codepoint atom, whose canonical content is the codepoint integer encoded as little-endian uint32:

```
codepoint_atom_id(c: u32) := BLAKE3(le32(c))
```

For example, the codepoint U+006B ('k', LATIN SMALL LETTER K) has atom hash `BLAKE3(0x6B 0x00 0x00 0x00)`. Same content, same hash, anywhere it appears.

Other modalities may admit additional atom types if their canonical content has a defined byte representation:

| Atom type | Canonical content | Hash input |
|---|---|---|
| `codepoint` | Unicode codepoint (u32) | `le32(c)` |
| `pixel_value` (optional) | RGBA channels (4× u8) | `(r, g, b, a)` |
| `audio_sample` (optional) | Single PCM sample (i16 or i32) | sample bytes in canonical endianness |
| `tensor_element` (optional) | Single tensor scalar | dtype + value bytes (BF16, F32, F64) |
| ~~`embedding_firefly`~~ — DEPRECATED | ~~Token entity post-Laplacian projection~~ — fireflies are POINTZM physicalities attached to existing `word_form` content entities (one POINTZM per ingested model per token, per [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VII), NOT separate atom-class entities. The 4-coordinate projection still holds; what changes is the storage shape — physicality on the species, not a new species. See AP-29. | ~~`(eig2, eig3, eig4, ||row||)` as 4× f64~~ — same projection coords, stored as POINTZM in the firefly partition of `substrate.physicality` attached to `entity_hash` of the token's `word_form`. |

Whether to admit modality-specific atom types is a design decision (see `Atom vocabulary` below). Text content always bottoms at codepoint atoms.

## Composition identity (Merkle DAG)

A composition is a node whose identity is derived from its ordered children. The hash function is straightforward Merkle:

```
composition_id([child_1_hash, child_2_hash, ..., child_n_hash]) :=
    BLAKE3(child_1_hash || child_2_hash || ... || child_n_hash)
```

The order matters: `[walk, er]` and `[er, walk]` produce different composition hashes. The trajectory geometry preserves the same order in its `linestring4d`, so both representations agree.

Recursive case: a parent composition's hash uses its children's hashes regardless of whether those children are atoms or compositions:

```
parent_id := BLAKE3(child_a_hash || child_b_hash || child_c_hash)
   where child_a is an atom (codepoint hash)
   where child_b is a composition (recursive hash)
   where child_c is another composition
```

The Merkle property: if any descendant changes, the parent's hash changes. Same descendants in same order → same parent hash, regardless of how deep the DAG goes.

## Edge identity

Edges are typed n-ary relations. Identity is over (edge_type_id, role-ordered participants):

```
edge_id(edge_type_id, [(role_a, entity_a_hash), (role_b, entity_b_hash), ...]) :=
    sort_by_role(participants) ->
    BLAKE3(le32(edge_type_id) || entity_hashes_in_role_order)
```

`role` is an enum from `ref.edge_role` (source, target, context, mediator, evidence, head, dependent). The role determines participant ordering; the order determines the hash.

Example: a `has_sense` edge from a lemma to a synset has role-ordered participants `[(source, lemma_hash), (target, synset_hash)]`. The identity is `BLAKE3(le32(has_sense_id) || lemma_hash || synset_hash)`.

Two `has_sense` edges between the SAME lemma and synset (regardless of how many sources attest) have the SAME edge identity. Multiple sources attesting the same edge produce ONE edge row plus multiple `relation_evidence` rows (one per attesting source).

A `has_sense` edge AND a `co_occurrence` edge between the same two entities have DIFFERENT edge identities (different `edge_type_id`). They're stored as different edge rows. This is correct: they're attestations of different relationships.

## Why edge type is in identity (and atom/composition type is not)

This is the most subtle identity decision. It's worth being explicit about why.

**For atoms and compositions:** Type would conflict with convergence. If `entity_type_id` were in the hash, the codepoint U+006B ('k') represented as a codepoint atom would have a different hash than the same byte sequence represented as a "letter" atom. Different sources might tag the same content as different types, fragmenting evidence. So entity type is on the row but NOT in the hash.

**For edges:** Type IS the relationship. `hypernym(cat, mammal)` and `co_occurrence(cat, mammal)` are genuinely different attestations of (potentially) different relationships. Treating them as the same edge would lose semantic information. So edge type IS in the edge hash.

**For evidence accumulation:** Each edge can have multiple `relation_evidence` rows from different sources. WordNet attests `hypernym(cat, mammal)` and a marine biology textbook ALSO attests `hypernym(cat, mammal)` — same edge, two evidence rows. Both contribute to the edge's significance via Glicko-2 in the relevant arena.

**Convergence happens at the entity level, not the edge level.** When WordNet attests `hypernym(whale, mammal)` and Llama emits `embedding_similarity(whale, mammal)`, those are TWO edges (different types). Both reference the same `whale` and `mammal` entity rows because content addressing made them converge there. Cross-edge-type significance aggregation in the relevant arenas (e.g., `semantic_relevance`) is what produces the substrate's "consensus on whale-mammal relationship" — not single-edge convergence.

## Placement metadata never enters identity

Placement metadata is information about WHERE or WHEN content was observed, not what the content IS:

| Metadata | What it represents | Where it lives |
|---|---|---|
| `filename` | which file the content came from | `provenance` row, `has_source` edge |
| `source_offset` | byte offset within the source | `provenance` metadata, NOT identity |
| `tensor_name` | name of the tensor in safetensors | `in_model` edge metadata |
| `line_number` | line in source file | provenance metadata |
| `timestamp` | when the content was ingested | `provenance.ingested_at` |
| `tenant_id` / `user_id` | who submitted the content | `provenance` row, access-control filter |
| `ordinal_position` | position within parent composition | linestring4d vertex position |

For ordinal position specifically: the ordinal is encoded by vertex order in the composition's `linestring4d`. It's NOT part of the composition's identity. Two compositions with the same children in the same order have the same hash, regardless of which substrate session created them, regardless of which file they came from.

## What "convergence" actually means mechanically

When two sources observe the same content, the substrate's identity functions return the same hash for both observations. The pipeline's `INSERT ... ON CONFLICT (hash) DO NOTHING` semantics ensure exactly one row exists per unique hash. Subsequent queries see one entity, not two. Multiple structural classifications of the same content (e.g. `dog` as both `word_form` and `lemma`) materialize as multiple rows in `substrate.entity_classification` against the same `entity_hash`, never as duplicate entity rows.

What convergence does NOT mean:
- It does NOT mean "fuzzy matching." Different content (even slightly different) produces different hashes and different rows.
- It does NOT mean "semantic similarity." Two strings that mean the same thing but use different bytes (e.g., `cafe` vs `café` vs `e + combining acute`) produce different hashes and different rows. They link via UCD's canonical decomposition mapping (an explicit edge), not via merging.
- It does NOT mean "case-insensitive matching." `Apple` and `apple` are different content, different hashes, different rows. They link via UCD's case-folding mapping (an explicit `case_folds_to` edge from UCD), not via merging.
- It does NOT mean "lossy compression." Same bytes always produce the same hash; lossless reconstruction is preserved.

## Hashing precision: BLAKE3-256 vs BLAKE3-128

BLAKE3 produces 256 bits of output by default. Storage cost per hash:

- 256 bits = 32 bytes per hash row reference
- 128 bits = 16 bytes per hash row reference

For a substrate with billions of edges and entities, the difference is non-trivial. A 1B-edge substrate using 256-bit hashes consumes ~64GB just for edge hash columns; with 128-bit hashes, ~32GB. Compounded across all referencing tables (significance, edge_member, physicality, junction tables), the savings can reach hundreds of gigabytes.

Collision probability:

- 128-bit hash: collision becomes likely after ~2^64 ≈ 1.8 × 10^19 distinct entities (birthday paradox)
- 256-bit hash: collision becomes likely after ~2^128 ≈ 3.4 × 10^38 distinct entities

For a substrate at 10^10 distinct entities, 128-bit collision probability is approximately `10^10 × 10^10 / 2^128 ≈ 3 × 10^-19` — astronomically small but nonzero. 256-bit collision probability is `10^10 × 10^10 / 2^256 ≈ 8 × 10^-58` — entirely negligible.

**Recommendation:** Use BLAKE3-128 (16-byte hashes) for substrate identity. Storage savings outweigh the negligible collision risk. The substrate's identity functions truncate BLAKE3-256 output to first 128 bits.

If 128-bit collision risk is unacceptable for a particular use case (e.g., regulatory requirements demand 256-bit collision resistance), the schema is parameterized to support 256-bit hashes via the `hash_value` domain in the schema. Switching is a migration, not an architectural change.

## Why BLAKE3 specifically

- **SIMD acceleration.** AVX-512, AVX2, SSE 4.1, NEON — all supported. Hashing is not a bottleneck on modern CPUs.
- **Tree mode for parallelism.** BLAKE3's internal Merkle structure means hashing a single large input parallelizes well across cores.
- **Length-extension resistance** (vs SHA-256 which has the property but unsuited to keyed hashing without HMAC). The substrate doesn't need HMAC, but BLAKE3's modern design avoids the legacy quirks.
- **Performance.** ~10× faster than SHA-256 for typical workloads on modern hardware.
- **Standard, audited.** BLAKE3 is widely deployed (rsync, IPFS, several blockchain projects) and has had multiple independent audits.

## Atom vocabulary: codepoints-only vs multi-modality

Two valid positions on what counts as an atom:

**Position A: Codepoints only.** Every digital artifact decomposes to compositions of Unicode codepoint atoms. A pixel value of 255 is the composition `[U+0032, U+0035, U+0035]` (the digit codepoints). An audio sample is a composition of digit codepoints. The substrate has exactly one atom type. This preserves perfect convergence: the literal string `255` and the pixel value 255 both decompose to the same composition.

**Position B: Multi-atom-type.** Modalities admit modality-specific atom types: `pixel_value`, `audio_sample`, `tensor_element` are atom types with their own canonical content. Convergence holds within a modality but the digit-codepoint string `255` is a different entity than the pixel value 255.

**Trade-off:** Position A is purer; Position B is more storage-efficient. A 1920×1080×3 RGB image as Position A produces ~6M digit-codepoint compositions per channel ≈ 18M for the image. As Position B it produces ~6M pixel-value atoms (with content addressing collapsing repeated pixel values, often substantially). Position B's storage advantage is measured in orders of magnitude.

**Recommendation for current implementation:** Position B (multi-atom-type), with a careful note that the cross-modality convergence claim ("the literal `255` is the same as a pixel value 255") is forfeit. The compensation is that within each modality, convergence is preserved. Cross-modality alignment uses explicit edges (e.g., a `numeric_value` edge linking the literal `255` text composition to the pixel-value atom 255), not identity.

This decision is documented and the trade-off is explicit. Future refactor to Position A is a migration, not an architectural rewrite, since the schema treats `entity_type_id` as a partition key independently from the hash.

## Concurrency and identity

Multiple decomposers ingesting in parallel can simultaneously try to insert the same entity (same hash). The substrate's correctness here relies on:

1. **Pre-insert dedup.** The pipeline batches inserts and checks `entity` table for existing hashes before COPY. Most duplicates are eliminated here.
2. **UNIQUE constraint as safety net.** `substrate.entity` has PRIMARY KEY on `(hash)`. If two pipeline processes both think a hash is missing and both try to insert, exactly one succeeds; the other gets a constraint violation that translates to a no-op via `ON CONFLICT DO NOTHING`.
3. **PostgreSQL MVCC.** Concurrent readers see consistent snapshots; the deduplication is transparent to inference workloads.

The combination is correct at any concurrency. There is no race condition because identity is content-addressed and the constraint catches simultaneous duplicate attempts.

## Cross-references

- The substrate laws governing identity: `10-architecture/01-substrate-laws.md` (Laws 1, 2, 6)
- The geometry that complements identity: `10-architecture/03-geometry-4d.md`
- Schema reference for identity columns: `20-technical/00-schema-reference.md`
- Native extension API for identity functions: `20-technical/01-native-extension-api.md`
- Decomposer contract requiring correct identity computation: `10-architecture/05-decomposer-contract.md`
