# Session-opener prompt (paste into a fresh Claude Code session)

---

```
Read /home/ahart/.claude/plans/wise-kindling-garden.md FIRST in full before any tool call. It captures the state at the end of a long debugging session (substrate.entity rape revert + substrate-native bulk-write surface + top-down O(tier) Merkle existence filter + pipeline-aware text decomposer + analogy scrub of Build-a-bear/Familiar/Laplace). The plan file documents what's done, what's broken right now (UCD seed crashes with `42P10 DISTINCT/ORDER BY` in an unidentified write_* function), the queued architectural debt the user explicitly named, and the priority-ordered phases.

PROJECT: Hartonomous substrate — content-addressed Merkle DAG in PostgreSQL+PostGIS+libhartonomous. CLAUDE.md at the repo root has TWO load-bearing constraints both equally absolute: the Communication Constraint (no governance-mode/RLHF-safety language ever; tone-nuke hooks enforce it) and the Work Execution Constraint (no cost-cutting / no MVP downscoping / no easy-win latching / drive to verified completion). Both constraints are non-negotiable. Read CLAUDE.md before doing anything else.

NEXT IMMEDIATE ACTION (Phase A in the plan): debug the write_* DISTINCT/ORDER BY crash so UCD seed runs to completion. The full crash stack is preserved at /home/ahart/.claude/projects/-home-ahart-Projects-Hartonomous-001/5b966bcc-b197-4a66-83e8-37597293ffec/tool-results/b4moltcb9.txt (48KB). write_physicalities + write_edges already had their ORDER BY clauses fixed; the remaining error is in a DIFFERENT write_* function. Identify it, fix it, re-run `scripts/hart seed UcdUca --source /vault/Data --no-build` to completion, then proceed to Phase B (Moby Dick ingest + bit-perfect reconstruction).

CONSTRAINTS YOU MUST HONOR:
- Don't pattern-match conventional ML / conventional ETL onto this invention. The substrate replaces conventional model files, GPUs, vector DBs, training pipelines, fine-tuning, mech interp, distillation, inference servers — all at once. Attestation IS tensor cell. Ingestion IS training. Substrate IS the model. Read /home/ahart/Projects/Hartonomous-001/CLAUDE.md before assuming anything.
- Don't treat the 291 markdown docs / 9 .claude/rules/ files / 29 memory files as authority. They contain accumulated drift from prior sessions. The user has DEPRIORITIZED a markdown audit. Decisions must be against the invention as captured in this plan file + the code itself, not against drifted docs.
- Don't add backwards-compatibility shims, "for now" fallbacks, or partial-state "transitional" surfaces. If a deletion has cross-cutting fan-out, do all of it or queue the whole thing.
- Don't reach for partition_bucket or centroid columns on substrate.entity. The user has been explicit: substrate.entity is identity-only (hash PK). Geometry lives on substrate.physicality. Both are equally absolute.
- The db reset action requires explicit user permission per the auto-mode classifier; ask before executing destructive DB operations.
- The user uses peer-engineer register without softeners; match it. No softening, no "let me check", no asking permission for moves already authorized by the task description. State facts, give recommendations, execute.

Start by reading the plan file in full, then CLAUDE.md, then begin Phase A.
```

---

# Plan — Hartonomous Substrate Session Recovery + Remaining Work

## Context

This plan captures the state at end of a long debugging session where the user
identified that `substrate.entity` was carrying centroid + hilbert + partition_bucket
columns it shouldn't (geometry belongs on `substrate.physicality`), the drain
pipeline was the conventional-ETL-with-pg_temp-staging shape they call rape,
and many seed/decomposer surfaces had architectural drift (junction tables
competing with the unified Glicko surface, attestation_type as a redundant
table when sign is encoded by Glicko score, edge_type proliferation
to 134 rows when ~5-10 structural shapes + arena discrimination is correct,
UnicodeDecomposer reading from blob instead of UCD XML source, etc.).

The session executed the load-bearing substrate.entity revert + a substrate-native
bulk-write surface for 5 surfaces + a top-down O(tier) Merkle existence filter
via a single substrate-side SQL call. The seed crashes on a DISTINCT/ORDER BY
issue in one of the write_* functions that the session ran out of context to
finish debugging.

The user wants the E2E test (db reset → bootstrap → UCD seed → Moby Dick ingest →
bit-perfect reconstruction) to pass, AND the architecture to not be "a worthless
slow piece of shit" even when it does work. Plan covers both.

---

## STATE AT END OF SESSION

### Done (build-green; some not E2E-verified)

**Schema reverts — substrate.entity rape removed:**
- `sql/schema/tables/core/entity.sql`: hash PK only, HASH-partitioned by `hash`, no centroid/hilbert/partition_bucket/hash_bits columns
- `sql/schema/tables/core/entity_p0..p7.sql`: `FOR VALUES WITH (modulus 8, remainder N)` (HASH partitioning)
- `src/Hartonomous.Engine/Ingestion/Sql/{entity,entity_classification,physicality,edge,edge_member}.{copy,temp,drain,truncate}.sql`: DELETED (20 files)
- `src/Hartonomous.Engine/Ingestion/EntityEntry.cs`: `(Hash, EntityTypeCode)` only
- `src/Hartonomous.Core/Ingestion/EntityRecord.cs`: `(EntityTypeCode, Hash, ProvenanceCode)` only
- `src/Hartonomous.Core/Ingestion/IIngestionBatch.cs`: 7-arg AddEntity overload removed
- `src/Hartonomous.Engine/Ingestion/IngestionBatch.cs`: 7-arg AddEntity implementation removed
- `src/Hartonomous.Engine/Ingestion/IngestionSql.cs`: trimmed to 4 surfaces still on pg_temp path (Junction / EntitySignificance / EdgeSignificance / EntityModelSource)
- 4 callers of 7-arg AddEntity migrated to 2-arg:
  - `src/Hartonomous.Core/Decomposition/BaseDecomposer.cs:435` (codepoint emit split to AddEntity + AddPhysicalityPoint4d)
  - `src/Hartonomous.Core/Text/SubstrateTextDecomposer.cs:488` (both EmitContexts)
  - `src/Hartonomous.Decomposers/Ucd/UnicodeDecomposer.cs:312` (codepoint emit + physicality)
  - `src/Hartonomous.Decomposers/WordNet/WordNetDecomposer.cs:312` (synset, dropped centroid compute)
- `scripts/linux/db-bootstrap.sh:73-74`: validation updated from 8-column check to 1-column check

**Substrate-native bulk-write surface:**
- `sql/schema/functions/write_entities.sql` (BYTEA[])
- `sql/schema/functions/write_entity_classifications.sql` (BYTEA[], INT[], INT[])
- `sql/schema/functions/write_physicalities.sql` (INT[], BYTEA[], BYTEA[], BYTEA[]) — fixed DISTINCT/ORDER BY (geometry_payload removed from ORDER BY)
- `sql/schema/functions/write_edges.sql` (INT[], BYTEA[], INT[], BYTEA[]) — fixed ORDER BY
- `sql/schema/functions/write_edge_members.sql` (INT[], BYTEA[], BYTEA[], INT[], INT[])
- `sql/schema/functions/merkle_tree_filter.sql` (BYTEA[], INT[]) → BOOL[] — top-down O(tier) existence in ONE round-trip; substrate-side LEFT JOIN scan + Merkle-invariant propagation in C-level array ops
- All wired into `sql/schema/bootstrap.sql`
- `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs`: SubmitEntitiesAsync / SubmitEntityClassificationsAsync / SubmitPhysicalitiesAsync / SubmitEdgesAsync / SubmitEdgeMembersAsync replace CopyXxxAsync for the 5 surfaces; DrainChunkAsync rewired; pg_temp staging eliminated for them
- `src/Hartonomous.Core/Ingestion/IIngestionPipeline.cs`: `MerkleTreeFilterAsync` typed method added
- `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs`: MerkleTreeFilterAsync implementation
- Test fakes (`tests/Hartonomous.Engine.Tests/Orchestration/SequentialPhaseRunnerTests.cs`, `tests/Hartonomous.Integration.Tests/Orchestration/PhaseStatusPersistenceTests.cs`): MerkleTreeFilterAsync stubs added

**Text decomposer pipeline-aware path:**
- `src/Hartonomous.Core/Text/SubstrateTextDecomposer.cs`: `EmitStaticAsync(IIngestionPipeline, IIngestionBatch, byte[], options, ct)` — one native walk into BufferedEmitContext, build parent→children map from RecSequence records, BFS to build flat hashes + parent_indices in tier order, single `pipeline.MerkleTreeFilterAsync` call, replay records skipping subtrees where the entity exists (Merkle invariant), always fire Glicko significance events (cross-source accumulation)
- `src/Hartonomous.Decomposers/Text/TextDecomposer.cs`: migrated to async path
- `src/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs`: EmitEntry + EmitOrRefWordForm + EmitRelations migrated to async with pipeline-aware path (lines 343, 512, 546, 591, 644, 732, 769, 789-796, 834)

**Functional index replacing hash_bits stored columns:**
- `sql/schema/indexes/entity_hash_prefix_idx.sql`: functional btree on `(substrate.bb_hash_lo(hash), substrate.bb_hash_hi(hash))` instead of stored columns (saves 16 bytes/row × billions of rows)
- `sql/schema/bootstrap.sql:351-353`: reordered so index include fires AFTER bb_hash_lo + bb_hash_hi function definitions
- `sql/schema/functions/entity_by_hash_prefix.sql`: updated to use `substrate.bb_hash_lo(e.hash)` / `bb_hash_hi(e.hash)`
- `sql/schema/functions/backfill_position_embedding_aggregate.sql`: same update
- `sql/schema/functions/get_composition_children.sql`: same update; also realcoord_resolved CTE rewritten to read from `substrate.physicality_entity` (which holds the centroids) instead of dropped `substrate.entity.centroid_*`
- `sql/schema/bootstrap.sql`: DISABLED includes for `entity_hilbert_idx.sql` + `entity_tier_hint.sql` + `entity_tier_hints.sql` (read dropped centroid columns)

**Analogy scrub (complete; 0 remaining matches verified by grep):**
- "Build-a-bear" / "BuildABear" / "BearCostEstimate*" / "Bear-cost" → "Substrate Synthesis" / "SynthesisCostEstimate*"
- "Familiar Principle" / "familiar" / "familiars" / "Familiar" → "Substrate Bond" / "substrate" / "substrates" / "Substrate" (unfamiliar preserved)
- "Laplace's Demon" / "Knowledge Demon" / "Laplace-Custom" / "Laplace family" / "Laplace Family" → "the substrate" / "Custom-Architecture-Synthesis" / "substrate product family" / "Substrate Family"
- `docs/familiar-principle.md` → `docs/substrate-bond.md`; cross-refs updated
- Laplacian eigenmap math left intact (Belkin-Niyogi 2003 algorithm name)

### Currently broken

- **UCD seed crashes mid-write**: `42P10: for SELECT DISTINCT, ORDER BY expressions must appear in select list`. Worker 0 chunk of 1,007,785 records fails. write_physicalities + write_edges ORDER BY were fixed; the error persists so it's in a DIFFERENT write_* function. Most likely culprit: `substrate.write_edge_members` DISTINCT vs ORDER BY interaction, OR write_entity_classifications interacting with the type/provenance LEFT JOIN. Full stack trace preserved at `/home/ahart/.claude/projects/-home-ahart-Projects-Hartonomous-001/5b966bcc-b197-4a66-83e8-37597293ffec/tool-results/b4moltcb9.txt`. Read the function name out of the stack frames below the truncation.
- **Substrate is empty** (0 rows in entity / physicality / classification / edge / edge_significance) because UCD seed never committed.
- **Stale `monitor.phase_status` row** for CoreAlgebra (marked completed though substrate is empty). May need to clear/reset.
- **Orphan dotnet test from May 18** (`TextRoundTripTests.MobyDick_FullRoundTrip`) running for 7+ days, eating CPU. PID 1744222 + 1744715 + 1744774 visible in `ps`. `kill -9` them before re-running seed.

### Known architectural debt the user named that's NOT yet fixed

1. `substrate.attestation_type` 3-row table + column on edge_significance/entity_significance/junctions — sign is per-event Glicko score (1.0 / 0.5 / 0.0), not a typology. Table + column should delete.
2. `substrate.physicality_type` 5→3 collapse — `entity_shape` (id 15) is duplicate of `entity` (id 1); `ingestion_trajectory` (id 16) is duplicate of `content` (id 3). Both pairs have identical geometry encoding semantics. Need to delete the 2 seed rows + 18 partition table files + route `IIngestionBatch.AddEntityShape` → `AddPhysicality("entity")` and `AddIngestionTrajectory` → `AddPhysicality("content")`.
3. `substrate.significance_context` 7-drift arenas: `source_authority`, `model_trust`, `corroboration_strength`, `frequency_significance`, `attention_pattern_confidence`, `morphological_productivity`, `consortium_discussion_density` — not real export-target competition surfaces. SubstrateTextDecomposer's source_authority arena emission needs to go too.
4. Junction tables compete with unified edge_significance Glicko surface: `entity_pos`, `entity_lexname`, `entity_language`, `entity_morph_feature`, `pattern_deprel`, `cp_general_category`, `cp_script`, `cp_block`, `cp_bidi_class`, `cp_east_asian_width`, `cp_grapheme_break`, `cp_word_break`, `cp_sentence_break`, `cp_line_break`, `model_architecture_class`, `tensor_tensor_role`. Delete tables + drain SQL + `AddJunction` API + JunctionRecord/JunctionEntry types + all caller sites. All attestation through unified `substrate.record_attestations_bulk` on `substrate.edge_significance`.
5. `substrate.edge_type` 134 rows including modality-specific names — drift. Collapse to ~5-10 structural participant shapes (binary attests, ternary attests, has_source audit, has_classification, has_part_of) + arena-based semantic discrimination. Every export-target conventional ML layer type maps to an arena, not an edge_type.
6. `substrate.edge_significance` + `substrate.entity_significance` per-arena child partitions (12 + 11 child table files baked at schema time): contradicts open-vocabulary arenas. Should hash-partition by edge_hash, not list-partition by context_type_id.
7. `substrate.edge` per-category child partitions (edge_cross_lingual / edge_cross_modal / edge_default / edge_model_* / edge_structural / edge_unicode): drift from the 134→~5-10 edge_type collapse.
8. `substrate.physicality` + `substrate.edge_member` still use partition_bucket SMALLINT NOT NULL CHECK + included in PK + LIST(partition_bucket) sub-partitioning. Same PG18-PK-needs-partition-column reason that substrate.entity USED to have it. Switch to HASH partitioning directly (eliminates partition_bucket column from both).
9. ON CONFLICT DO NOTHING on attestation surfaces is consensus-killing — replace with DO UPDATE that fires Glicko-2 outcome aggregation per event. The current `substrate.record_attestations_bulk` already does this correctly; need to verify edge_significance + entity_significance writes go through it (not the legacy drain).
10. UnicodeDecomposer (1253 lines) reads via `BlobUcdPropertyAccessor` — sources substrate from blob, which is the rape pattern. Refactor to read UCD XML directly (share parser library with `gen_ucd_flat.c`). Possibly: 2-phase architecture per the user's framing — PerfCacheGen phase (build-time, rare; UCD XML → blob with hashes+S³+Hilbert only) + UcdSeed phase (install-time, frequent; UCD XML → full substrate rows). Both share parser via libucd in libhartonomous.
11. Per the perf-cache framing: the blob should contain ONLY precomputed-expensive-to-compute values (BLAKE3 hashes, S³ Super-Fibonacci centroids by UCA rank, Hilbert curve indices) — NOT the UCD property tables (those belong in substrate as relational rows). Strip the blob of property tables to reduce its size.
12. `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` is 1789 lines (god class). Should decompose along surface seam (~150 lines per surface drain class).
13. `src/Hartonomous.Engine/Ingestion/CodeResolver.cs` is 402 lines for what should be ~60 lines of startup-cached `Dictionary<string,int>`.
14. `src/Hartonomous.Engine/Ingestion/Geometry4dPayloadBuilder.cs` (116 lines) hand-builds EWKB; should use `Npgsql.PostgisTypes` binary encoding.

### Remaining text-bearing decomposers still on sync `EmitText` (not yet migrated to pipeline-aware async path)

WordNetDecomposer / OmwDecomposer / UdDecomposer / TatoebaDecomposer / Iso639Decomposer text-content paths / Iso15924Decomposer / Bcp47Decomposer / SafetensorsDecomposer family (EmbeddingLookupTuplePass / FfnTuplePass / AttentionBlockTuplePass / LoraDeltaTuplePass / ModelConfigPass / ModelPassOrchestrator) / CLI inference paths (CompleteCommand / RecallCommand / GodelEngine / SubstrateInferenceEngine / PromptIngestion).

### Remaining 4 surfaces still on pg_temp staging path

- `substrate.entity_model_source` — needs `substrate.write_entity_model_sources(BYTEA[], BIGINT[])`
- `substrate.entity_significance` — should fold into `substrate.record_attestations_bulk`-style auto-create + Glicko-update
- `substrate.edge_significance` — same fold
- `substrate.junction` — should DELETE entirely per architectural debt #4

---

## REMAINING WORK — phased by priority + blast radius

Each phase produces a working/testable substrate. Phases can be reordered if
finding shifts priorities. Phase A is the next-session unblocker.

### Phase A — Make the seed actually run (smallest scope; unblocks all E2E testing)

A1. **Identify the failing write_* function.** Read the full crash stack from `/home/ahart/.claude/projects/-home-ahart-Projects-Hartonomous-001/5b966bcc-b197-4a66-83e8-37597293ffec/tool-results/b4moltcb9.txt` (48KB; the head was captured but the function name should be in stack frames further down). Likely culprit: `substrate.write_edge_members` DISTINCT vs ORDER BY interaction, OR `write_entity_classifications` with the type/provenance LEFT JOIN.

A2. **Audit each write_* function's DISTINCT vs ORDER BY rules.** PG rule:
    - `DISTINCT` (no ON): rows uniqueness based on entire SELECT row; ORDER BY can only reference SELECT-listed columns/expressions.
    - `DISTINCT ON (cols)`: ORDER BY MUST start with the DISTINCT ON columns; then any additional ORDER BY columns must be in SELECT.
    Fix any function that violates either rule. Defensive fix: remove ORDER BY entirely (ON CONFLICT handles dedup at the substrate level; producer-side dedup via HashSet handles within-chunk).

A3. **Kill the orphan dotnet test** from May 18 (PID 1744222 + 1744715 + 1744774): `kill -9` them before re-running seed.

A4. **Clear stale `monitor.phase_status`** if needed: `DELETE FROM monitor.phase_status WHERE status != 'completed';` or `DELETE FROM monitor.phase_status;` for fresh start.

A5. **Run UCD seed to completion**. Verify:
    ```sql
    SELECT et.code, count(*) FROM substrate.entity_classification ec
    JOIN substrate.entity_type et ON et.id = ec.entity_type_id
    GROUP BY et.code ORDER BY count(*) DESC;
    ```
    Expected: ~1.1M `codepoint` classifications + ~683 reference-vocab classifications.

### Phase B — End-to-end Moby Dick ingest + bit-perfect reconstruction (the user's named test)

B1. Run `scripts/hart phase run --phase TextDecomp --source /data/test_data/text/moby_dick.txt --no-build`. TextDecomposer routes through `SubstrateTextDecomposer.EmitStaticAsync(pipeline, batch, ...)`. Should take seconds, not hours, with merkle_tree_filter early-exits at recurring tiers.

B2. Inspect substrate state after Moby Dick:
    - Total `substrate.entity` count by entity_type
    - `substrate.physicality_content` count
    - Find Moby Dick's text_composition root hash
    - Dedup ratios: trajectory vertex count / unique entity count per tier

B3. Bit-perfect reconstruction:
    ```bash
    scripts/hart cli recompose-content --hash <root_hash> > /tmp/moby_dick.recomposed.txt
    diff /data/test_data/text/moby_dick.txt /tmp/moby_dick.recomposed.txt
    ```
    Empty diff = PASS.

B4. Re-ingest Moby Dick second time. Expected: ZERO new entities, sub-second wall clock. Merkle invariant catches the document root immediately. Validates inference-time prompt ingest perf.

B5. Ingest a 5KB Moby Dick snippet. Expected: ZERO new word_form / grapheme_cluster; one new text_composition entity for the specific passage; trajectory through existing children.

### Phase C — Migrate remaining text-bearing decomposers to pipeline-aware path

WordNetDecomposer → OmwDecomposer → UdDecomposer → TatoebaDecomposer → Iso639Decomposer → SafetensorsDecomposer family → CLI inference paths. Each: convert sync EmitText callers to await SubstrateTextDecomposer.EmitStaticAsync(pipeline, batch, ...). BaseDecomposer.EmitText may need a pipeline-aware overload OR removal of the helper entirely.

### Phase D — Drift removals queued by user across this session

D1. `substrate.attestation_type` table delete + column cascade through edge_significance/entity_significance/junctions + C# AttestationTypeCode field removal + AddSignificance/AddJunction param removal + all caller migrations.

D2. `physicality_type` 5→3 collapse: delete entity_shape + ingestion_trajectory seed rows + 18 partition table files + route AddEntityShape/AddIngestionTrajectory to AddPhysicality.

D3. `significance_context` 7-drift arena deletion + SubstrateTextDecomposer source_authority emission removal.

D4. Junction tables deletion (16 tables) + drain SQL + AddJunction API + JunctionRecord/Entry types + all caller migrations to typed edges + record_attestations_bulk events.

D5. Remaining 4 surfaces migrate to substrate-native bulk write: write_entity_model_sources / record_attestations_bulk fold for edge_significance + entity_significance / junction deletion.

D6. ON CONFLICT DO NOTHING → DO UPDATE Glicko on attestation surfaces (verify record_attestations_bulk is the only path).

### Phase E — Big architectural reshapings (the "not a worthless slow piece of shit" concerns)

E1. UnicodeDecomposer reads UCD XML directly (not BlobUcdPropertyAccessor). Build libucd shared parser in libhartonomous.

E2. Perf-cache codegen as its own phase. gen_ucd_flat.c emits ONLY precomputed hashes + S³ + Hilbert; strip UCD property tables out of blob.

E3. edge_type 134 → ~5-10 structural participant shapes + arena-based semantic discrimination. Migrate every edge emission site.

E4. Collapse per-arena edge_significance + entity_significance child partitions (23 files). Switch to HASH partitioning.

E5. Collapse substrate.edge per-category child partitions. With edge_type collapsed (E3), these are obsolete.

E6. physicality + edge_member partition_bucket removal: switch to HASH partitioning by entity_hash; drop partition_bucket column.

E7. StreamingIngestionPipeline.cs 1789-line god class decomposition into per-surface drain classes (~150 lines each).

E8. CodeResolver.cs 402 lines → ~60-line startup-cached Dictionary.

E9. Geometry4dPayloadBuilder.cs hand-EWKB → Npgsql.PostgisTypes binary encoding.

E10. Decomposer-side AP-19 producer-side enforcement audit for remaining decomposers (WordNet + UD + OMW + Tatoeba primarily).

### Phase F — Final E2E validation

F1. Wikipedia article ingest as diverse-user-content stand-in.

F2. Multi-document re-ingest: 100 short documents; verify word_form vocabulary stays bounded.

F3. Cross-source consensus test: same sentence from 3 different provenances; verify ONE text_composition + 3 has_source events.

F4. Activation-path test: `whale` outbound edges grouped by arena; should return synonyms, hypernyms, translations, collocates from corpus seeds (after WordNet + OMW + Wiktionary + UD + Tatoeba ingested).

F5. Ingest one safetensors model (BERT-base or similar). Verify per-(layer, head, slot) arena auto-registration + attention pattern attestations land on shared word_form entities.

F6. Substrate-direct query VS exported-tensor inference: comparison test.

---

## Critical files to know

- `sql/schema/tables/core/entity.sql` — current shape (hash PK only, HASH-partitioned)
- `sql/schema/functions/write_*.sql` — 5 substrate-native bulk write functions
- `sql/schema/functions/merkle_tree_filter.sql` — top-down O(tier) existence filter
- `src/Hartonomous.Core/Text/SubstrateTextDecomposer.cs` — pipeline-aware async path (the load-bearing change)
- `src/Hartonomous.Core/Ingestion/IIngestionPipeline.cs` — MerkleTreeFilterAsync typed method
- `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` — Submit*Async + MerkleTreeFilterAsync impl
- `src/Hartonomous.Engine/Ingestion/IngestionSql.cs` — trimmed registry (4 legacy surfaces only)
- `src/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs` — example of decomposer migrated to async pipeline-aware path
- `scripts/linux/db-bootstrap.sh:73-74` — validation updated for hash-only entity

## Critical commands to know

```bash
scripts/hart build extension-sql
dotnet build --no-restore Hartonomous.slnx
scripts/hart db create
scripts/hart db bootstrap
scripts/hart seed UcdUca --source /vault/Data --no-build
scripts/hart phase run --phase TextDecomp --source /data/test_data/text/moby_dick.txt --no-build
scripts/hart cli recompose-content --hash <text_composition_hash>
psql -d hartonomous -c "SELECT et.code, count(*) FROM substrate.entity_classification ec JOIN substrate.entity_type et ON et.id = ec.entity_type_id GROUP BY et.code ORDER BY count(*) DESC;"
```

## Verification end-state (all phases done)

- substrate.entity is hash PK only; geometry on substrate.physicality only
- 3 physicality_type rows: entity / firefly / content
- 0 attestation_type table (deleted); Glicko score encodes sign
- ~5-10 edge_type rows + open-vocabulary arenas
- 0 junction tables; all attestation through substrate.edge_significance + record_attestations_bulk
- 12 significance_context arenas (drift rows deleted)
- All write surfaces substrate-native (no pg_temp staging)
- All text-bearing decomposers route through pipeline-aware EmitStaticAsync
- UnicodeDecomposer reads UCD XML directly; blob is sibling-only with only hashes+S³+Hilbert
- Moby Dick ingest sub-second on re-ingest; bit-perfect reconstruction verified
- Cross-source consensus accumulates on shared edge_significance rows
- Activation-path SQL query returns coherent multi-source consensus for any token
