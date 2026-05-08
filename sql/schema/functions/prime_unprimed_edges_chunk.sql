-- substrate.prime_unprimed_edges_chunk(p_arena_id, p_chunk_size)
--
-- Phase-owned significance primer. The caller resets the per-arena scan at
-- the start of a priming pass, then this function advances over
-- substrate.edge's PK index in bounded chunks. ON CONFLICT makes re-scanning
-- already-primed edges idempotent while still catching later phases that add
-- lower edge_type_id values.
--
-- Watermark-based forward scan over substrate.edge's PK index
-- (edge_type_id, hash). Per-arena state lives in
-- substrate.arena_priming_state. NO anti-join, NO merge join, NO spill —
-- the previous LEFT JOIN/IS NULL/LIMIT shape over partitioned tables is
-- exactly what triggered PG18's batched-HashJoin slot mismatch
-- (nodeHashjoin.c:1099-1115 vs ExecJustOuterVarVirt) → SIGSEGV/SIGABRT.
--
-- Compound formula matches prime_edge_significance_for_staging:
--   μ₀ = COALESCE(pea.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay)
--   σ₀ = COALESCE(pea.initial_sigma, p.initial_sigma)
CREATE OR REPLACE FUNCTION substrate.prime_unprimed_edges_chunk(
    p_arena_id   INT,
    p_chunk_size INT DEFAULT 4096
) RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_last_etid   INT;
    v_last_hash   BYTEA;
    v_inserted    BIGINT;
    v_max_etid    INT;
    v_max_hash    BYTEA;
    v_chunk_count INT;
BEGIN
    INSERT INTO substrate.arena_priming_state (context_type_id)
    VALUES (p_arena_id)
    ON CONFLICT (context_type_id) DO NOTHING;

    SELECT last_edge_type_id, last_hash
      INTO v_last_etid, v_last_hash
      FROM substrate.arena_priming_state
     WHERE context_type_id = p_arena_id
       FOR UPDATE;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    SELECT
        p_arena_id,
        nc.edge_type_id,
        nc.hash,
        COALESCE(
            pea.initial_mu,
            p.initial_mu * et.semantic_weight * p.derivation_decay
        ),
        COALESCE(pea.initial_sigma, p.initial_sigma),
        0.06,
        0
      FROM (
            SELECT e.edge_type_id, e.hash, e.provenance_id
              FROM substrate.edge e
             WHERE (e.edge_type_id, e.hash) > (v_last_etid, v_last_hash)
             ORDER BY e.edge_type_id, e.hash
             LIMIT p_chunk_size
           ) AS nc
      JOIN substrate.edge_type   et ON et.id = nc.edge_type_id
      JOIN substrate.provenance  p  ON p.id  = nc.provenance_id
      LEFT JOIN substrate.provenance_edge_authority pea
        ON pea.provenance_id = p.id
       AND pea.edge_type_id  = nc.edge_type_id
    ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    SELECT sub.edge_type_id, sub.hash, sub.cnt
      INTO v_max_etid, v_max_hash, v_chunk_count
      FROM (
            SELECT edge_type_id,
                   hash,
                   COUNT(*) OVER () AS cnt
              FROM substrate.edge
             WHERE (edge_type_id, hash) > (v_last_etid, v_last_hash)
             ORDER BY edge_type_id, hash
             LIMIT p_chunk_size
           ) sub
     ORDER BY edge_type_id DESC, hash DESC
     LIMIT 1;

    IF v_max_etid IS NULL THEN
        UPDATE substrate.arena_priming_state
           SET completed  = TRUE,
               updated_at = now()
         WHERE context_type_id = p_arena_id;
    ELSE
        UPDATE substrate.arena_priming_state
           SET last_edge_type_id = v_max_etid,
               last_hash         = v_max_hash,
               completed         = (v_chunk_count < p_chunk_size),
               updated_at        = now()
         WHERE context_type_id = p_arena_id;
    END IF;

    -- Return rows scanned, not rows inserted. A chunk can legitimately scan
    -- only already-primed rows; returning inserted rows would falsely signal
    -- completion and leave later edges unvisited.
    RETURN COALESCE(v_chunk_count, 0);
END $$;

COMMENT ON FUNCTION substrate.prime_unprimed_edges_chunk(INT, INT) IS
    'Per-arena significance primer chunk. Returns rows scanned so callers continue through conflict-only chunks; uses a watermark forward scan over substrate.edge PK index and avoids the anti-join shape that triggered PG18 batched-HashJoin slot mismatch.';
