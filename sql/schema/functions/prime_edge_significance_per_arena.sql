-- substrate.prime_edge_significance_for_staging() — per-arena loop rewrite.
--
-- The CROSS JOIN against substrate.significance_context multiplied each
-- staging row by the arena count (10 today, open-vocabulary tomorrow) into
-- one giant INSERT. With UD-scale batches landing on top of an existing
-- 1.4M-row partitioned target, the resulting plan tipped a Postgres backend
-- into stack-smashing (SIGABRT/glibc canary) — likely from large parallel
-- worker tuple buffers in the partition routing path.
--
-- This rewrite replaces the single CROSS JOIN INSERT with a plpgsql FOR
-- loop over substrate.significance_context. Each iteration emits one
-- INSERT bounded to a single arena (a single partition of
-- substrate.edge_significance). Total work is identical, but:
--
--   * Each INSERT touches exactly one partition — no partition-routing
--     buffer pressure.
--   * Each INSERT's tuple set is N (staging size), not 10×N.
--   * Loop body is plpgsql — Postgres can't pick a parallel plan that
--     spawns workers across all arenas at once.
--   * If one arena fails, the loop variable tells us which one.
--
-- Same compound formula (μ = COALESCE(pea, p × et.weight × p.decay),
-- σ = COALESCE(pea, p.σ)). Same open-vocabulary behavior — adds new
-- arenas automatically. Same idempotence via ON CONFLICT DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.prime_edge_significance_for_staging()
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_arena_id   INT;
    v_arena_code TEXT;
    v_inserted   BIGINT := 0;
    v_loop_rows  BIGINT;
BEGIN
    -- jit + parallel workers disabled at function-level (SET clauses above).
    -- The single-INSERT CROSS JOIN form crashed Postgres backends with stack
    -- canary failure in ExecInterpExpr (gdb traced into /dev/shm/PostgreSQL
    -- DSM segment — parallel-worker territory). Single-process, no-JIT
    -- execution sidesteps both vectors. Cost of doing this serially is small:
    -- partition routing into one partition at a time, btree PK probe per row.
    FOR v_arena_id, v_arena_code IN
        SELECT id, code FROM substrate.significance_context ORDER BY id
    LOOP
        INSERT INTO substrate.edge_significance
            (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
        SELECT
            v_arena_id,
            e.edge_type_id,
            e.hash,
            COALESCE(
                pea.initial_mu,
                p.initial_mu * et.semantic_weight * p.derivation_decay
            ) AS mu,
            COALESCE(
                pea.initial_sigma,
                p.initial_sigma
            ) AS sigma,
            0.06,
            0
          FROM staging_edge s
          JOIN substrate.edge e
            ON e.edge_type_id = s.edge_type_id
           AND e.hash         = s.hash
          JOIN substrate.edge_type   et ON et.id = e.edge_type_id
          JOIN substrate.provenance  p  ON p.id  = e.provenance_id
          LEFT JOIN substrate.provenance_edge_authority pea
            ON pea.provenance_id = p.id
           AND pea.edge_type_id  = e.edge_type_id
            ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

        GET DIAGNOSTICS v_loop_rows = ROW_COUNT;
        v_inserted := v_inserted + v_loop_rows;
    END LOOP;

    RETURN v_inserted;
END $$;

COMMENT ON FUNCTION substrate.prime_edge_significance_for_staging() IS
    'Per-batch: prime substrate.edge_significance with compound-formula μ and σ. Loops over substrate.significance_context one arena at a time so each INSERT routes to exactly one partition (avoids partition-routing buffer pressure that triggered SIGABRT in the prior CROSS JOIN form). Open-vocabulary, idempotent via ON CONFLICT DO NOTHING.';
