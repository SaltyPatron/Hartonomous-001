-- substrate.entity_tier_hints — bulk variant of substrate.entity_tier_hint.
-- Returns one row per hash with a stored centroid; NULL-centroid entities
-- (pre-trigger insert, no identity physicality yet) are omitted from result.
CREATE OR REPLACE FUNCTION substrate.entity_tier_hints(p_hashes substrate.hash_value[])
RETURNS TABLE (entity_hash substrate.hash_value, tier_hint DOUBLE PRECISION)
LANGUAGE sql
STABLE
AS $$
    SELECT e.hash,
           1.0 - sqrt(
               e.centroid_x * e.centroid_x +
               e.centroid_y * e.centroid_y +
               e.centroid_z * e.centroid_z +
               e.centroid_m * e.centroid_m)
      FROM substrate.entity e
      JOIN unnest(p_hashes) AS u(h) ON u.h = e.hash
     WHERE e.centroid_x IS NOT NULL;
$$;

COMMENT ON FUNCTION substrate.entity_tier_hints(substrate.hash_value[]) IS
    'Bulk variant of substrate.entity_tier_hint. NULL-centroid entities (pre-trigger insert, no identity physicality) are omitted from result rows.';
