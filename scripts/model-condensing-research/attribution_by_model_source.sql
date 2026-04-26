-- Research helper: attribute approximate substrate storage to a model source.
-- "Condensed file size" is NOT this: export bytes != PostgreSQL table bytes.
-- Dedup: shared entities (same BLAKE3) appear in multiple model sources; allocate by reference count or first contributor only in ad-hoc analysis.
--
-- Replace :model_source_id with a concrete id from substrate.model_source.
--
-- Example: sum TOAST-backed column sizes for physicality (adjust table/column names to match your migration).

-- SELECT relname FROM pg_class WHERE relname LIKE '%physicality%';

/*
SELECT
  ms.id AS model_source_id,
  COUNT(DISTINCT ems.entity_id) AS entity_count,
  SUM(pg_column_size(p.geom)) AS approx_geom_bytes
FROM substrate.model_source ms
JOIN substrate.entity_model_source ems ON ems.model_source_id = ms.id
JOIN substrate.entity e ON e.id = ems.entity_id
LEFT JOIN substrate.physicality p ON p.entity_id = e.id
WHERE ms.id = :model_source_id
GROUP BY ms.id;
*/

-- Generic pattern: per-entity row count × avg(pg_column_size(...)) in hot tables
-- (Fill in your partition names; physicality is LIST-partitioned in Hartonomous.)

SELECT
  'attribution_by_model_source.sql — template: wire entity_model_source to tables you care to measure'
  AS note;
