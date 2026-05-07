# The Streaming Ingestion Pipeline

There is one active ingestion write path: `StreamingIngestionPipeline` in `src/Hartonomous.Engine/Ingestion/`. Every decomposer, seed or runtime, is a pure producer that emits records to `IRecordSink`. The pipeline owns buffering, backpressure, database connections, COPY, deduplication, trajectory backfill, and significance priming.

The older `IIngestionPipeline` batch shape exists as compatibility surface only. It must not become a second architecture.

## Producer Contract

Decomposers emit substrate records. They do not own channels, transactions, lookup-wide entity resolution, staging tables, or significance priming.

Valid producer output categories are:

| Record | Purpose |
|--------|---------|
| Entity | Insert one hash-only row into `substrate.entity`. |
| Entity classification | Attach structural type evidence in `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`. |
| Edge | Insert a typed relation keyed by `(edge_type_id, hash)`. |
| Edge member | Attach role-ordered participants by `entity_hash`. |
| Junction | Populate reference-layer evidence such as `entity_pos`, `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, or `pattern_deprel`. |
| Physicality | Store `geometry(GeometryZM)` in `substrate.physicality`. |
| Sequence | Store reconstruction order in `substrate.sequence(parent_hash, ordinal, child_hash, rle_count)`. |
| Entity significance | Prime content trust in a specific arena. |
| Edge significance | Bulk primed by the phase-owned post-pass unless explicitly carried by a producer-supported override. |
| Entity model source | Record model-source observations without changing entity identity. |

## Pipeline Responsibilities

The pipeline owns ten bounded channels, one per record kind. Each drain task holds a long-lived `NpgsqlConnection`, writes rows to a session-local `pg_temp.*_inflight` table with binary COPY, then performs a set-based `INSERT ... SELECT` into canonical substrate tables with `ON CONFLICT DO NOTHING`.

There are no persistent `substrate.staging_*` tables in the active design. Temp inflight tables are scoped to the drain connection and disappear when the connection closes.

## Identity and Deduplication

`substrate.entity` has one column: `hash`. Structural type is not part of the entity row and never enters the identity hash. A word form, lemma, text composition, tensor, or codepoint is classified through `substrate.entity_classification` and reference/junction tables.

Producer-side deduplication drops within-session duplicates before COPY. Cross-session duplicates are accepted into the inflight table and discarded by `ON CONFLICT DO NOTHING` during the set-based insert.

## Geometry and Significance

Entity physicality uses PostGIS `geometry(GeometryZM)` for all modalities. Edge trajectories are built inline when participant centroids are available in the batch; otherwise the phase runner invokes the explicit trajectory backfill after all decomposers for the phase finish.

Edge significance is primed by the phase-owned post-pass across every current row in `substrate.significance_context`. Do not hard-code the starter arena list.

## Forbidden Drift

- Do not add decomposer-owned channels or `Parallel.ForEachAsync` ingestion loops.
- Do not reintroduce persistent staging tables or staging drain functions.
- Do not make producers resolve surrogate entity ids. Hashes are the foreign keys.
- Do not call significance priming from a producer or from `FlushAsync`.
- Do not insert structural type, POS, sense, language, or placement metadata into `substrate.entity`.
