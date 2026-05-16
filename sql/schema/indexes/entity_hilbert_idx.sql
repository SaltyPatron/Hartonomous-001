-- BTREE on substrate.entity.hilbert_index for log-N range scans by 4D
-- spatial locality. The Hilbert curve preserves locality: adjacent
-- hilbert values correspond to spatially-adjacent 4D points. Range
-- queries `WHERE hilbert_index BETWEEN $a AND $b` scan a 4D-spatial
-- box-like region.
--
-- Combined with the entity's radial tier (sqrt(x²+y²+z²+m²) — atoms ≈ 1,
-- documents ≈ 0), Hilbert ordering gives both ANGULAR (semantic direction)
-- and RADIAL (abstraction depth) locality in one B-tree scan.
CREATE INDEX IF NOT EXISTS entity_hilbert_idx ON substrate.entity (hilbert_index);

COMMENT ON INDEX substrate.entity_hilbert_idx IS
    '4D Hilbert-curve ordering of substrate.entity centroids. Range scans cluster entities by 4D spatial proximity, which combines angular direction (semantic similarity at atom tier) AND radial tier (Merkle DAG depth — atoms on glome, documents at origin). Enables log-N spatial-locality queries without per-row geometry computation.';
