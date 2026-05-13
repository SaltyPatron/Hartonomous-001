-- 4D geometric realization of an entity. geometry4d is the substrate
-- geometry carrier (POINT4D for atoms, LINESTRING4D for compositions).
-- Per-partition CHECK constraints enforce the dimensionality each
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
    geom                geometry4d NOT NULL,
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
    -- FK to substrate.entity(hash) application-enforced — pipeline batch
    -- ordering writes entities before physicalities. (PG18.3 partitionwise-FK
    -- SEGV pattern conservatively avoided.)
) PARTITION BY LIST (physicality_type_id);

COMMENT ON TABLE substrate.physicality IS
    'Geometric realizations of entities. Native geometry4d. Hash-only entity reference (no type_id). Composition child identity, ordinal, and RLE metadata live on the physicality row.';
