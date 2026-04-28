-- 4D geometric realization of an entity. POSTGIS-native GeometryZM throughout
-- (POINTZM for atoms, LINESTRINGZM for compositions, with M as a real spatial
-- axis). Per-partition CHECK constraints enforce the dimensionality each
-- physicality_type expects. content_hash distinguishes multiple physicalities
-- of the same type for the same entity (e.g., multiple firefly samples).
CREATE TABLE substrate.physicality (
    physicality_type_id INT  NOT NULL REFERENCES substrate.physicality_type(id),
    entity_type_id      INT  NOT NULL,
    entity_hash         substrate.hash_value NOT NULL,
    content_hash        substrate.hash_value NOT NULL,
    geom                geometry(GeometryZM) NOT NULL,
    PRIMARY KEY (physicality_type_id, entity_type_id, entity_hash, content_hash)
    -- Composite FK to substrate.entity (entity_type_id, hash) omitted —
    -- partitionwise-FK SEGV pattern under PG18.3 bulk INSERT. Application
    -- layer (NpgsqlIngestionPipeline.SubmitBatchAsync) writes entities
    -- before physicalities in every batch.
) PARTITION BY LIST (physicality_type_id);

COMMENT ON TABLE substrate.physicality IS
    'Geometric realizations of entities. PostGIS GeometryZM with M as a real spatial axis. Per-partition CHECKs enforce subtype dimensionality. content_hash deduplicates within (type, entity) for multi-sample geometries. FK to substrate.entity is application-enforced.';
