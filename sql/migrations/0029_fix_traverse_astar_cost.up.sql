-- Migration 0029: Fix traverse_astar cost function
-- The original (0026) had two bugs:
--   1. Cost queried edge significance (s.edge_id = e.id), but ALL significance rows
--      in the substrate are entity-based (entity_id set, edge_id NULL). This meant
--      every edge got uniform 0.001 cost → traversal was effectively random BFS.
--   2. A corrupted/duplicated block in the INSERT INTO _trav_frontier.
--
-- Fix: use the NEIGHBOR ENTITY's significance to compute step cost.
-- Higher mu = more significant entity = lower traversal cost = preferred path.

CREATE OR REPLACE FUNCTION traverse_astar(
    p_seed_id            bigint,
    p_target_type_id     int,
    p_arena_id           int,
    p_max_depth          int              DEFAULT 5,
    p_max_results        int              DEFAULT 100,
    p_edge_type_filter   int              DEFAULT NULL,
    p_min_mu             double precision DEFAULT NULL
)
RETURNS TABLE (
    target_entity_id bigint,
    cost             double precision,
    path             bigint[],
    edge_path        bigint[]
)
LANGUAGE plpgsql VOLATILE ROWS 100
AS $$
DECLARE
    v_count     int := 0;
    v_neighbor  record;
    v_cur_eid   bigint;
    v_cur_cost  double precision;
    v_cur_path  bigint[];
    v_cur_epath bigint[];
    v_cur_depth int;
BEGIN
    CREATE TEMP TABLE _trav_frontier (
        f_entity_id   bigint,
        f_cost        double precision,
        f_path        bigint[],
        f_edge_path   bigint[],
        f_depth       int
    ) ON COMMIT DROP;

    CREATE TEMP TABLE _trav_visited (
        f_entity_id   bigint PRIMARY KEY
    ) ON COMMIT DROP;

    -- Seed.
    INSERT INTO _trav_frontier VALUES (p_seed_id, 0.0, ARRAY[p_seed_id], ARRAY[]::bigint[], 0);
    INSERT INTO _trav_visited VALUES (p_seed_id);

    WHILE EXISTS (SELECT 1 FROM _trav_frontier) AND v_count < p_max_results LOOP
        -- Pop lowest-cost frontier node.
        SELECT f.f_entity_id, f.f_cost, f.f_path, f.f_edge_path, f.f_depth
        INTO v_cur_eid, v_cur_cost, v_cur_path, v_cur_epath, v_cur_depth
        FROM _trav_frontier f
        ORDER BY f.f_cost ASC
        LIMIT 1;

        DELETE FROM _trav_frontier
        WHERE ctid = (
            SELECT ctid FROM _trav_frontier
            WHERE f_entity_id = v_cur_eid AND f_cost = v_cur_cost
            LIMIT 1
        );

        -- If we've reached max depth, yield this path but don't expand.
        IF v_cur_depth >= p_max_depth THEN
            IF p_target_type_id = 0 OR EXISTS (
                SELECT 1 FROM substrate.entity WHERE id = v_cur_eid AND entity_type_id = p_target_type_id
            ) THEN
                target_entity_id := v_cur_eid;
                cost := v_cur_cost;
                path := v_cur_path;
                edge_path := v_cur_epath;
                RETURN NEXT;
                v_count := v_count + 1;
            END IF;
            CONTINUE;
        END IF;

        -- Yield the current node if it matches target type (and is not the seed).
        IF v_cur_depth > 0 AND (p_target_type_id = 0 OR EXISTS (
            SELECT 1 FROM substrate.entity WHERE id = v_cur_eid AND entity_type_id = p_target_type_id
        )) THEN
            target_entity_id := v_cur_eid;
            cost := v_cur_cost;
            path := v_cur_path;
            edge_path := v_cur_epath;
            RETURN NEXT;
            v_count := v_count + 1;
            IF v_count >= p_max_results THEN EXIT; END IF;
        END IF;

        -- Expand neighbors via edge_member.
        -- Cost = 1 / neighbor_entity_significance_mu (lower cost = higher significance = preferred).
        -- Significance is entity-based (all 2.9M rows have entity_id, not edge_id).
        FOR v_neighbor IN
            SELECT
                em2.entity_id AS neighbor_id,
                e.id AS eid,
                COALESCE(
                    (SELECT 1.0 / NULLIF(s.mu, 0)
                     FROM substrate.significance s
                     WHERE s.entity_id = em2.entity_id
                       AND s.context_type_id = p_arena_id
                     LIMIT 1),
                    0.001  -- default cost for entities without significance in this arena
                ) AS step_cost
            FROM substrate.edge_member em1
            JOIN substrate.edge e ON em1.edge_id = e.id
            JOIN substrate.edge_member em2 ON e.id = em2.edge_id AND em2.entity_id <> em1.entity_id
            WHERE em1.entity_id = v_cur_eid
              AND (p_edge_type_filter IS NULL OR e.edge_type_id = p_edge_type_filter)
              AND (p_min_mu IS NULL OR EXISTS (
                  SELECT 1 FROM substrate.significance s
                  WHERE s.entity_id = em2.entity_id
                    AND s.context_type_id = p_arena_id
                    AND s.mu >= p_min_mu
              ))
        LOOP
            IF NOT EXISTS (SELECT 1 FROM _trav_visited WHERE f_entity_id = v_neighbor.neighbor_id) THEN
                INSERT INTO _trav_visited VALUES (v_neighbor.neighbor_id);
                INSERT INTO _trav_frontier VALUES (
                    v_neighbor.neighbor_id,
                    v_cur_cost + v_neighbor.step_cost,
                    v_cur_path || v_neighbor.neighbor_id,
                    v_cur_epath || v_neighbor.eid,
                    v_cur_depth + 1
                );
            END IF;
        END LOOP;
    END LOOP;
END;
$$;
