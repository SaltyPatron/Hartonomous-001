-- Count edges whose relation trajectory has not been populated.
CREATE OR REPLACE FUNCTION substrate.count_missing_edge_trajectories()
RETURNS BIGINT
LANGUAGE sql STABLE
AS $$
    SELECT count(*)::BIGINT
      FROM substrate.edge
     WHERE geom IS NULL;
$$;

COMMENT ON FUNCTION substrate.count_missing_edge_trajectories() IS
    'Count substrate edges with NULL geom. Phase post-passes use this as the fail-loud semantic gate for edge trajectory completeness.';
