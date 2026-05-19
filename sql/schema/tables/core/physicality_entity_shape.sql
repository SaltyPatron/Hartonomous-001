-- physicality_type_id = 15, code = 'entity_shape'.
--
-- Real-coord canonical-shape geometry. Answers the question: "what does
-- this entity look like in 4D as a structural fingerprint?"
--
-- For atoms (no children): POINTZM at the modality's anchor coord —
-- codepoint Super-Fibonacci S^3 unit-quaternion by UCA collation rank
-- from the pre-gen UCD blob; audio sample at signal coord; pixel channel
-- at intensity coord; tensor cell at value coord. All four (X, Y, Z, M)
-- are real metric coords. No mantissa packing. No bitmask payload.
--
-- For compositions (any tier of any modality): LINESTRINGZM (or
-- MULTILINESTRINGZM for branching shapes) whose vertices ARE the
-- children's identity POINTZM centroids in canonical order. Each vertex
-- is a real metric coord in the parent's 4D frame. Fréchet / Hausdorff
-- matchable; gist_geometry_ops_nd R-tree-indexed.
--
-- Modality lives on the attached entity's entity_type (recovered via
-- substrate.entity_classification join). The partition itself is
-- modality-agnostic; per-axis meaning derives from the modality of the
-- entity it attaches to.
--
-- Companion partition: physicality_ingestion_trajectory (id 16) holds
-- the mantissa-packed recomposition recipe for the same composition
-- entity. A composition typically has both rows present — one in each
-- partition — answering different queries.
CREATE TABLE substrate.physicality_entity_shape
    PARTITION OF substrate.physicality FOR VALUES IN (15)
    PARTITION BY LIST (partition_bucket);

ALTER TABLE substrate.physicality_entity_shape
    ADD CONSTRAINT physicality_entity_shape_geom
    CHECK (
        GeometryType(geom) IN (
            'POINT', 'LINESTRING', 'MULTILINESTRING',
            'POLYGON', 'MULTIPOLYGON', 'MULTIPOINT',
            'GEOMETRYCOLLECTION'
        )
        AND ST_NDims(geom) = 4
    );

COMMENT ON TABLE substrate.physicality_entity_shape IS
    'Real-coord canonical-shape geometry. POINTZM for atoms at modality anchor coords; LINESTRINGZM through children identity POINTZM centroids for compositions. Modality recovered from entity_classification. Fréchet / Hausdorff matchable.';
