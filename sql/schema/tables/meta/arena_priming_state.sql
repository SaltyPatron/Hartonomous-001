-- Per-arena progress watermark for substrate.prime_unprimed_edges_chunk.
-- The backfill primer scans substrate.edge starting from
-- (last_edge_type_id, last_hash) using the (edge_type_id, hash) PK index.
-- This replaces the LEFT JOIN/IS NULL anti-join shape that triggered
-- PG18's batched-HashJoin slot mismatch (nodeHashjoin.c:1099-1115 vs
-- ExecJustOuterVarVirt) → SIGSEGV/SIGABRT.
CREATE TABLE IF NOT EXISTS substrate.arena_priming_state (
    context_type_id   INT  PRIMARY KEY
        REFERENCES substrate.significance_context(id) ON DELETE CASCADE,
    last_edge_type_id INT  NOT NULL DEFAULT 0,
    last_hash         BYTEA NOT NULL DEFAULT '\x'::BYTEA,
    completed         BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
