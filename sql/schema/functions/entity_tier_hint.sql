-- substrate.entity_tier_hint — derive an approximate Merkle DAG depth from
-- the entity's stored 4D centroid radius. Atoms (codepoints) project to the
-- unit 4-sphere (glome) — Super-Fibonacci by UCA collation rank produces
-- ||p||₄d = 1 ± float noise. Compositions are arithmetic means of children's
-- centroids, so by Jensen + sphere convexity, compositions land STRICTLY
-- INSIDE the unit 4-ball. Mean of N points on the glome has expected norm
-- ~1/√N — the more constituents, the closer to origin.
--
-- The tier hint is `1 - radius`: atoms ≈ 0, deep documents ≈ 1.
-- Use for substrate-native "give me high-tier entities near angular X" queries
-- without joining substrate.entity_classification.
--
-- Returns NULL if the entity has no stored centroid yet (e.g., pre-trigger
-- inserts, or after backfill skipped the entity due to no identity physicality).
CREATE OR REPLACE FUNCTION substrate.entity_tier_hint(p_hash substrate.hash_value)
RETURNS DOUBLE PRECISION
LANGUAGE sql
STABLE
AS $$
    SELECT CASE
             WHEN e.centroid_x IS NULL THEN NULL
             ELSE 1.0 - sqrt(
                 e.centroid_x * e.centroid_x +
                 e.centroid_y * e.centroid_y +
                 e.centroid_z * e.centroid_z +
                 e.centroid_m * e.centroid_m)
           END
      FROM substrate.entity e
     WHERE e.hash = p_hash;
$$;

COMMENT ON FUNCTION substrate.entity_tier_hint(substrate.hash_value) IS
    'Approximate Merkle DAG depth derived from 4D centroid radius. Atoms (codepoints on the glome) → 0; deep compositions (documents near origin) → 1. Substrate-native tier query without joining entity_classification — the substrate''s hierarchical structure is realized geometrically via Super-Fibonacci S³ projection + arithmetic-mean centroid recursion. Bulk variant: substrate.entity_tier_hints(hash[]).';
