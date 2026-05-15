-- Physicality type 15: entity_shape. The building block's own canonical
-- structural fingerprint in real metric coordinates.
--
-- For atoms-with-internal-structure (tensor γ-scale shapes, codec codebook
-- contours, etc. — entities whose physicality is their own real-coord
-- internal shape rather than a trajectory through other entities), this is
-- a LINESTRINGZM through the per-feature values laid out in the partition's
-- declared axis convention.
--
-- For compositions (word_form, lemma, morpheme, grapheme_cluster, sentence
-- shapes, document silhouettes, etc.), this is the canonical shape derived
-- from the children's real-coord centroids in role / sequence order.
-- POINTZM when a single canonical centroid suffices, LINESTRINGZM for
-- one-segment shapes, MULTILINESTRINGZM for multi-tier or branching
-- canonical fingerprints (e.g. a sentence's word-tier and grapheme-tier
-- views packaged in one row).
--
-- Distinct from physicality_ingestion_trajectory (id 16): entity_shape
-- vertices are REAL metric coordinates for Fréchet / Hausdorff shape
-- matching (rhyme detection, idiomaticity divergence, frayed-edge surveys,
-- application-fault pattern matching). ingestion_trajectory vertices are
-- mantissa-packed identity-POINTZMs for O(tier) reconstruction via the
-- entity_by_hash_prefix composite-btree. The two surfaces answer different
-- queries; both can coexist on the same entity_hash with distinct
-- physicality_type_id rows.
CREATE TABLE substrate.physicality_entity_shape
    PARTITION OF substrate.physicality FOR VALUES IN (15);
ALTER TABLE substrate.physicality_entity_shape
    ADD CONSTRAINT physicality_entity_shape_geom
    CHECK (GeometryType(geom) IN ('POINT', 'LINESTRING', 'MULTILINESTRING')
           AND ST_NDims(geom) = 4);
