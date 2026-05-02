-- 4D geometric realization of an entity. PostGIS-native GeometryZM
-- (POINTZM for atoms, LINESTRINGZM for compositions, M as a real spatial
-- axis). Per-partition CHECK constraints enforce the dimensionality each
-- physicality_type expects. content_hash distinguishes multiple physicalities
-- of the same type for the same entity (e.g., multiple firefly samples).
--
-- Hash-only entity reference (Phase C of unification refactor):
-- substrate.entity has a hash-only PK, so physicality references entities
-- by hash alone. No entity_type_id column.
CREATE TABLE substrate.physicality (
    physicality_type_id INT  NOT NULL REFERENCES substrate.physicality_type(id),
    entity_hash         substrate.hash_value NOT NULL,
    content_hash        substrate.hash_value NOT NULL,
    geom                geometry(GeometryZM) NOT NULL,
    PRIMARY KEY (physicality_type_id, entity_hash, content_hash)
    -- FK to substrate.entity(hash) application-enforced — pipeline batch
    -- ordering writes entities before physicalities. (PG18.3 partitionwise-FK
    -- SEGV pattern conservatively avoided.)
) PARTITION BY LIST (physicality_type_id);

COMMENT ON TABLE substrate.physicality IS
    'Geometric realizations of entities. PostGIS GeometryZM. Hash-only entity reference (no type_id). Partitioned by physicality_type_id. FK to substrate.entity application-enforced.';
