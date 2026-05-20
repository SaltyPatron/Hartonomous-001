# Plan: Convergent Refactor — Resume Document

## Design (authoritative — supersedes everything below)

The substrate's representation, per the user's design:

- **One physicality row per entity.** No junction tables for parent-child. No `substrate.sequence`. No `substrate.trajectory_child`. Both were agent-side hallucinations and are forbidden names.
- **Atoms:** `physicality.geom` = POINTZM at the atom's real centroid (codepoint S³ Super-Fibonacci by UCA rank; audio sample (time, freq, mag, phase); image pixel (x, y, intensity, class); etc.).
- **Compositions:** `physicality.geom` = LINESTRINGZM (single-segment) or MULTILINESTRINGZM (branching). Each vertex encodes a child entity's identity via the mantissa-packing contract:
  - X mantissa = child hash bits 0..51 (`bb_pack_hash_lo`)
  - Y mantissa = ordinal + RLE bit-banged (`bb_pack_ordinal_rle`)
  - Z mantissa = child hash bits 52..103 (`bb_pack_hash_hi`)
  - M mantissa = metadata (`bb_pack_metadata`)
  - Inverse: `(int64_t)(d - 2^52)`. C# `MantissaPacking` and SQL `bb_*` share this contract byte-for-byte.
- **Resolution:** vertex coordinates → `bb_unpack_*` → JOIN `substrate.entity` composite btree on `(hash_bits_0_51, hash_bits_52_103)`. GENERATED-ALWAYS columns on `substrate.entity` carry the prefixes; no app-side index maintenance.
- **Cross-composition reverse lookup ("where does whale appear?"):** PostGIS R-tree bbox prefilter on X-coord values intersecting whale's `bb_pack_hash_lo` value, refined by exact unpack + composite btree resolution. No reverse-index table.
- **`substrate.entity.centroid_4d`** carries the entity's real-coord 4D representative POINTZM (atoms: content-derived centroid; compositions: recursive mean of children's `centroid_4d`). Drives `edge.geom` construction, recursive Merkle math, GiST k-NN queries.

That's it. Two columns of truth per entity: `physicality.geom` (identity-encoded for compositions, real-coord for atoms) and `entity.centroid_4d` (real-coord representative for all entities). No third relational table.

## Commits delivering the design

| Commit | Content |
|--------|---------|
| `ad1f0a4` | S1+S2: bb_pack/unpack SQL helpers; `substrate.entity.{hash_bits_0_51, hash_bits_52_103}` GENERATED columns + composite btree; `entity_by_hash_prefix` batched lookup; C# `MantissaPacking`; Blake3/PhysicalityEmitter extensions |
| `a9c4838` | S3.D chunk 1: `geometry4d` → `geometry(GeometryZM)` migration; partition CHECK rewrites; cast bridges; `composition_*` functions read mantissa-packed vertices from LINESTRINGZM geom |
| `f85612f` | S3.D chunk 2: `provenance.modality_codes` array → `substrate.provenance_modality` junction |
| `d863c68` | S3.D chunk 3: `substrate.entity.centroid_4d` transitional NULL-allowed pending pipeline migration |
| `c779960` | S3.D chunk 4: `libhartonomous` native trajectory walker kernel (`hartonomous_trajectory_unpack`) + P/Invoke binding |
| `0385f52` | S3.D chunk 5: `SubstrateTierWalker` concrete (delegates to `substrate.get_composition_children` + `substrate.recompose_text`) |

Schema concat clean. Core+Engine tests 263/263. Working tree clean at `0385f52`.

## Remaining work — atomic bites

Each is one commit. Each is independently verifiable (`git show`, schema concat, `dotnet build`, tests).

### Bite A — codepoint atom seed populates centroid_4d (smallest)

`sql/schema/functions/populate_codepoint_atoms.sql` + `populate_codepoint_atoms_chunk.sql`:
- Replace `ST_MakePoint4D(a.x, a.y, a.z, a.m)` (legacy custom geometry4d constructor) with PostGIS-native `ST_MakePoint(a.x, a.y, a.z, a.m)` returning POINTZM.
- Add `centroid_4d` to the `substrate.entity` INSERT column list; populate with the same `ST_MakePoint(a.x, a.y, a.z, a.m)`. The codepoint's S³ Super-Fibonacci position IS its real centroid; no separate computation.

Validates that the foundational atom set starts carrying real `centroid_4d` values for composition recursion to build on.

### Bite B — physicality array drop

Drop `physicality.{child_hashes, ordinal_starts, rle_counts}` columns + CHECK constraint. Pipeline writes only `geom` (LINESTRINGZM with mantissa-packed vertices, constructed at drain time via C# `MantissaPacking` or PostGIS function call).

Files:
- `sql/schema/tables/core/physicality.sql` — drop columns, drop CHECK, fix comment
- `src/Hartonomous.Engine/Ingestion/Sql/physicality.{temp,copy,drain}.sql` — drop columns
- `src/Hartonomous.Core/Ingestion/PhysicalityRecord.cs` — drop ChildHashes/OrdinalStarts/RleCounts
- `src/Hartonomous.Engine/Ingestion/PhysicalityEntry.cs` — drop equivalents
- `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` — drop array passing through drain
- `src/Hartonomous.Engine/Ingestion/IngestionBatch.cs` — drop AddPhysicality overloads that take arrays
- `sql/tests/{geom_4d_tests,brain_4d_tests}.sql` — drop array columns from INSERTs

If a code path constructs the LINESTRINGZM at drain time from arrays right now, that construction moves out — either to the decomposer (which already has access to child hashes and ordinals) or stays at drain time but reads only the geom column.

### Bite C — codepoint_property array drop

`sql/schema/tables/junctions/codepoint_property.{decomposition_mapping, full_case_fold}` INT[] columns are wrong. Each codepoint with a multi-CP decomposition or full case fold gets a composition entity in `substrate.entity` (Merkle hash over target codepoint hashes) with its own `physicality.geom` LINESTRINGZM (mantissa-packed vertices through target codepoint hashes). `codepoint_property` carries `decomposition_entity_hash` / `full_case_fold_entity_hash` BYTEA FK columns pointing at those composition entities.

Files:
- `sql/schema/tables/junctions/codepoint_property.sql` — drop arrays, add hash FK columns
- `sql/schema/functions/populate_codepoint_property_range_from_ext.sql` — emit composition entities + LINESTRINGZM physicality, populate FK columns
- `src/Hartonomous.Core/Decomposition/Unicode/*` (whatever C# consumer paths read these arrays) — switch to entity lookup

### Bite D — pipeline writes centroid_4d for non-codepoint entities

Bite A handles codepoints. For the rest of ingestion (word_forms, lemmas, sentences, etc.), the C# pipeline computes each entity's `centroid_4d` at insert time (atoms: content-derived; compositions: mean of children's centroids gathered via `PhysicalityEmitter.MeanCentroid`) and writes it into `substrate.entity.centroid_4d`. Then tighten the column to NOT NULL.

This is the gate that makes Bite C's "composition entity emission during UCD seed" path work cleanly (those composition entities need centroids too).

### Bite E — remaining cleanup

- Delete dead `ST_MakePoint4D` / `ST_MakeLine4D` extension functions if nothing in `substrate.*` references them after Bites A/B.
- Audit `physicality_type` seed entries; remove `entity_shape` / `ingestion_trajectory` if those classifications collapsed during the migration.
- Schema concat clean, dotnet build clean, tests green.

## Status (2026-05-14)

Bite A: pending (next).
Bites B–E: queued.

## Strict-don't list

Catalog of patterns the agent is forbidden from introducing during this refactor. Each one has been tried, rejected, and (in some cases) the wrong direction shipped to a commit before user correction. Listed here so future sessions don't re-rape.

- **No `substrate.sequence` table.** Deleted; not coming back. Composition ordering lives in `physicality.geom` vertex Y-mantissa.
- **No `substrate.trajectory_child` table** (or any other parent-child junction by any other name). Compositions encode children in `geom`. PostGIS R-tree + `entity` composite btree handle queries.
- **No array columns** on any `substrate.*` table. 1NF / FK / btree-indexability are non-negotiable. The "transitional" array pair (physicality, codepoint_property) is debt, not target state.
- **No "two trajectories per entity"** model. Each entity has ONE `physicality.geom` row. Atoms POINTZM real, compositions LINESTRINGZM mantissa-packed.
- **No round-trip via legacy `ST_MakePoint4D`** or `geometry4d` column type anywhere in `substrate.*`. PostGIS-native constructors only. `public.point4d` / `public.linestring4d` survive as internal native-kernel I/O ABI; they are not column types and not user-visible.
- **No "trajectory IS edges" reasoning** in newly-written code. The relation between vertices in a composition geom is content-addressed parent-child, NOT a `substrate.edge` row. Substrate edges are typed n-ary attestation relations between independently-identified entities (`has_sense`, `model_attention_pattern`, etc.) — distinct from composition structure.
- **No condescension in agent text output.** User is a 20-year senior engineer. Do not explain `git show`, `git diff`, basic schema migration, or anything else the user obviously knows. Keep agent text terse and assume peer-level fluency.
