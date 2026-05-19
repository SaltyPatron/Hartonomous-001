-- physicality_type_id = 3, code = 'content'.
--
-- Content trajectories — sequences of entity bricks. A text_composition
-- "the cat sat on the mat" is a LINESTRINGZM with 6 vertices:
--   V1 = pack(hash(the), ord=1, rle=1, meta=0)
--   V2 = pack(hash(cat), ord=2, rle=1, meta=0)
--   V3 = pack(hash(sat), ord=3, rle=1, meta=0)
--   V4 = pack(hash(on),  ord=4, rle=1, meta=0)
--   V5 = pack(hash(the), ord=5, rle=1, meta=0)   -- same hash as V1, distinct ordinal
--   V6 = pack(hash(mat), ord=6, rle=1, meta=0)
-- Same content dedupes to one entity referenced at multiple ordinals.
-- rle compresses runs.
--
-- The geometry IS the indexed child manifest at the content tier. The
-- walk stops at the first entity-tier brick — the brick's internal
-- structure lives in its own entity-partition physicality.
--
-- Reverse-resolve a vertex to its child brick by unpacking (X, Z) into
-- (hash_bits_0_51, hash_bits_52_103) and JOINing against the composite
-- btree on substrate.entity_by_hash_prefix — one bulk lookup recovers
-- the full child slice.
--
-- MULTILINESTRINGZM for discontinuous / branching / multi-stream
-- trajectories (footnote bodies interleaved with main text, bilingual
-- interlinear, etc.).
--
CREATE TABLE substrate.physicality_content
    PARTITION OF substrate.physicality FOR VALUES IN (3)
    PARTITION BY LIST (partition_bucket);
-- LINESTRING / MULTILINESTRING for ordered trajectories (text, audio,
-- code). POLYGON / MULTIPOLYGON for closed-region content (image
-- regions, video shots whose spatial extent matters more than order).
-- GEOMETRYCOLLECTION for mixed-tier content packages. All 4D.
ALTER TABLE substrate.physicality_content
    ADD CONSTRAINT physicality_content_geom
    CHECK (GeometryType(geom) IN (
              'LINESTRING', 'MULTILINESTRING',
              'POLYGON', 'MULTIPOLYGON',
              'GEOMETRYCOLLECTION')
           AND ST_NDims(geom) = 4);
