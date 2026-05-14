-- ONE physicality row per entity, carrying the entity's substrate-level
-- geometric expression. PostGIS-native geometry(GeometryZM) is the universal
-- storage; substrate.st_4d_* operators extend PostGIS to use the M dimension
-- (raw ST_Distance / ST_Centroid / ST_FrechetDistance drop M and are
-- forbidden — AP-4). Per-partition CHECK constraints enforce per-type
-- geometric shape (POINTZM for atoms, LINESTRINGZM / MULTILINESTRINGZM for
-- compositions).
--
-- Geometric expressions across the substrate (real coords only — NO mantissa
-- packing of identity bits into vertices; identity lives in the relational
-- child-tracking layer, geometry stores the canonical SHAPE):
--   * Atom physicality (codepoint S3 position, audio sample, image pixel,
--     etc.): geom = POINTZM at the atom's real centroid in its modality's
--     content-derived metric space. Codepoints: 4 real Super-Fibonacci S^3
--     unit-quaternion components by UCA collation rank
--     (`scripts/build/generate_unicode_tables.py:83,1080`).
--   * Entity physicality (word_form, lemma, morpheme — compositions over
--     atoms): geom = LINESTRINGZM whose vertices ARE the real centroids of
--     the children at the next tier down, in canonical role / sequence order.
--     For "cat" (word_form): three vertices = c.centroid, a.centroid,
--     t.centroid (each a real S^3 POINTZM read from its physicality_s3 row).
--   * Content physicality (text_composition, sentence, paragraph, document,
--     audio_chunk, image_region, video_shot — compositions of compositions):
--     geom = LINESTRINGZM whose vertices ARE the real centroids of the
--     constituent entities. For "the cat sat on the mat" (text_composition):
--     six vertices = the.centroid, cat.centroid, sat.centroid, on.centroid,
--     the.centroid, mat.centroid. Branching / discontinuous structure uses
--     MULTILINESTRINGZM. Identity of which children are referenced is
--     resolved through the relational child-tracking layer, NOT through
--     bit-decoding the geometry vertices (per memory
--     `feedback-no-mantissa-vertex-packing` and the S3.D chunk-1 corrected
--     model).
--
-- ST_Frechet / Hausdorff over two composition geoms compares geometric
-- trajectory SHAPE through real-coord space (analogy completion, frayed-edge
-- detection, application-fault matching) — not sequence-of-IDs equality.
--
-- content_hash distinguishes multiple physicalities of the same
-- (physicality_type, entity) — e.g., multiple firefly samples per token from
-- different models.
--
-- Hash-only entity reference: substrate.entity has a hash-only PK; physicality
-- references entities by hash alone. FK to substrate.entity(hash) is
-- application-enforced (pipeline batch ordering writes entities before
-- physicalities; PG18.3 partitionwise-FK SEGV pattern conservatively avoided).
CREATE TABLE substrate.physicality (
    physicality_type_id INT  NOT NULL REFERENCES substrate.physicality_type(id),
    entity_hash         substrate.hash_value NOT NULL,
    content_hash        substrate.hash_value NOT NULL,
    geom                geometry(GeometryZM) NOT NULL,
    -- TRANSITIONAL — child_hashes / ordinal_starts / rle_counts arrays held
    -- in place this delta while consumers (ingestion pipeline drain SQL,
    -- C# PhysicalityRecord, populate_codepoint_property_range_from_ext) are
    -- migrated to read composition children from the LINESTRINGZM geom's
    -- mantissa-packed vertices via substrate.get_composition_children. Once
    -- every consumer reads from geom, these columns are dropped in the
    -- follow-up atomic chunk (no array columns in substrate.* tables;
    -- 1NF / FK / btree-indexability discipline).
    child_hashes        substrate.hash_value[] NULL,
    ordinal_starts      INT[] NULL,
    rle_counts          INT[] NULL,
    CHECK (
        (child_hashes IS NULL AND ordinal_starts IS NULL AND rle_counts IS NULL)
        OR (
            child_hashes IS NOT NULL
            AND ordinal_starts IS NOT NULL
            AND rle_counts IS NOT NULL
            AND array_length(child_hashes, 1) = array_length(ordinal_starts, 1)
            AND array_length(child_hashes, 1) = array_length(rle_counts, 1)
        )
    ),
    PRIMARY KEY (physicality_type_id, entity_hash, content_hash)
) PARTITION BY LIST (physicality_type_id);

COMMENT ON TABLE substrate.physicality IS
    'ONE substrate-level geometric expression per entity. PostGIS geometry(GeometryZM); substrate.st_4d_* operators extend PostGIS to honor the M dimension. Atom geom = POINTZM real centroid; composition geom = LINESTRINGZM with ID-encoded vertices (mantissa packing — vertex IS the relational structure). content_hash distinguishes co-typed multi-source samples per entity. Array columns are transitional pending consumer migration; final state has only the geom column.';
