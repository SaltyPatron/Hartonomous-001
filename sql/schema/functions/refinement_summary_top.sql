CREATE OR REPLACE FUNCTION substrate.refinement_summary_top(
    p_model_arch_hash BYTEA,
    p_arena_code      TEXT DEFAULT 'corroboration_strength',
    p_limit           INT DEFAULT 25
)
RETURNS TABLE (
    tensor_hash     BYTEA,
    edge_type_code  TEXT,
    source_only_mu  FLOAT8,
    consensus_mu    FLOAT8,
    delta_mu        FLOAT8,
    above_threshold BOOLEAN
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT summary.tensor_hash,
           summary.edge_type_code,
           summary.source_only_mu,
           summary.consensus_mu,
           summary.delta_mu,
           summary.above_threshold
      FROM substrate.refinement_summary(p_model_arch_hash, p_arena_code) summary
     ORDER BY summary.delta_mu DESC NULLS LAST
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.refinement_summary_top(BYTEA, TEXT, INT) IS
    'Top-N refinement summary rows ordered by consensus delta for CLI/UI quote surfaces.';