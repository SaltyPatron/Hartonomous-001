-- Count edges whose relation trajectory failed to populate.
--
-- An edge "should" have geometry iff every one of its members resolves to a
-- physicality row (i.e. the participants are content entities the substrate
-- has projected into the 4D jar). Metadata edges (has_tensor, has_dtype,
-- has_shape, has_tensor_name, has_*_artifact, has_hidden_size, in_model,
-- ...) bind tensor / architecture entities that have no physicality of their
-- own — those edges legitimately carry NULL geom and are NOT a failure.
--
-- This function is the fail-loud semantic gate: it returns the count of
-- edges where every member has a centroid available AND we still failed to
-- compute a trajectory. Any non-zero result is a populate_edge_trajectories
-- bug or a substrate physicality gap on a content entity.
CREATE OR REPLACE FUNCTION substrate.count_missing_edge_trajectories()
RETURNS BIGINT
LANGUAGE sql STABLE
AS $$
    WITH null_edges AS (
        SELECT edge_type_id, hash AS edge_hash
          FROM substrate.edge
         WHERE geom IS NULL
    ),
    member_coverage AS (
        SELECT em.edge_type_id,
               em.edge_hash,
               count(*)                                          AS member_count,
               count(ph.has_phys) FILTER (WHERE ph.has_phys)     AS members_with_phys
          FROM null_edges ne
          JOIN substrate.edge_member em
            ON em.edge_type_id = ne.edge_type_id
           AND em.edge_hash    = ne.edge_hash
          LEFT JOIN LATERAL (
              SELECT TRUE AS has_phys
                FROM substrate.physicality ph
               WHERE ph.entity_hash = em.entity_hash
               LIMIT 1
          ) ph ON true
         GROUP BY em.edge_type_id, em.edge_hash
    )
    SELECT count(*)::BIGINT
      FROM member_coverage
     WHERE member_count >= 2
       AND members_with_phys = member_count;
$$;

COMMENT ON FUNCTION substrate.count_missing_edge_trajectories() IS
    'Count substrate edges with NULL geom whose members ALL have physicality (i.e. trajectory was computable but missing). Edges whose members lack physicality (metadata edges over tensor / architecture entities) are excluded — those legitimately carry NULL geom by construction.';
