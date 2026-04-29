-- Reverse 0020: restore anti-join primer and drop arena watermark state.
--
-- WARNING: down migration restores the buggy SQL shape that triggered
-- SIGSEGV/SIGABRT under PG18 batched HashJoin. Only run in tear-down
-- contexts.

DROP FUNCTION IF EXISTS substrate.prime_unprimed_edges_chunk(INT, INT);
DROP TABLE IF EXISTS substrate.arena_priming_state;

-- Restore the previous anti-join shape. Includes the SET clauses that
-- were on the old function (cargo-cult plan-disable bandaids that did
-- not actually fix the bug).
CREATE FUNCTION substrate.prime_unprimed_edges_chunk(
    p_arena_id   INT,
    p_chunk_size INT DEFAULT 4096
)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
SET enable_mergejoin = off
AS $$
DECLARE
    v_inserted BIGINT;
BEGIN
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    SELECT
        p_arena_id,
        e.edge_type_id,
        e.hash,
        COALESCE(
            pea.initial_mu,
            p.initial_mu * et.semantic_weight * p.derivation_decay
        ),
        COALESCE(pea.initial_sigma, p.initial_sigma),
        0.06,
        0
      FROM substrate.edge e
      JOIN substrate.edge_type   et ON et.id = e.edge_type_id
      JOIN substrate.provenance  p  ON p.id  = e.provenance_id
      LEFT JOIN substrate.provenance_edge_authority pea
        ON pea.provenance_id = p.id
       AND pea.edge_type_id  = e.edge_type_id
      LEFT JOIN substrate.edge_significance es
        ON es.context_type_id = p_arena_id
       AND es.edge_type_id    = e.edge_type_id
       AND es.edge_hash       = e.hash
     WHERE es.edge_hash IS NULL
     LIMIT p_chunk_size
    ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;

-- Restore previous drain_staging_edge_chunk (without edge_significance
-- cross-product).
CREATE OR REPLACE FUNCTION substrate.drain_staging_edge_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, edge_type_id, hash, provenance_id
          FROM substrate.staging_edge
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.edge (edge_type_id, hash, provenance_id)
        SELECT DISTINCT ON (edge_type_id, hash) edge_type_id, hash, provenance_id
          FROM claimed
        ON CONFLICT (edge_type_id, hash) DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_edge
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;
