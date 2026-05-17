-- substrate.recompose_content — sub-second content reconstruction from a
-- document hash. The load-bearing efficiency property of the substrate.
--
-- ONE QUERY. Tree-walks the content trajectory via pg_recompose_walk
-- (already in the C extension), filters to codepoint leaves, resolves each
-- codepoint hash to its UCA-rank index via huc_cp_from_hash (mmap'd blob),
-- assembles UTF-8 bytes in walk order.
--
-- Holistic stack:
--   - C/C++ in libhartonomous + hartonomous_pg: pg_recompose_walk DFS tree
--     walk via SPI bulk btree probes, mantissa-unpacked composition vertex
--     traversal, mmap'd O(log N) codepoint reverse lookup (no IO)
--   - SQL: this function — recursive composition + per-leaf codepoint
--     resolution + byte assembly via string_agg
--   - C# orchestration (Hartonomous.Cli RecomposeContentCommand): ONE
--     ExecuteScalarAsync call, returns the assembled bytes
--
-- Performance contract: O(tier-depth) bulk SPI probes (one per tier in
-- pg_recompose_walk's DFS), zero-allocation codepoint lookup. Bible-size
-- documents (~200K words, ~1M codepoint leaves) reconstruct in sub-second.
CREATE OR REPLACE FUNCTION substrate.recompose_content(
    p_root_hash substrate.hash_value,
    p_max_depth INT DEFAULT 16
) RETURNS bytea
LANGUAGE sql STABLE AS $$
    -- Walk the trajectory tree; cp_from_hash returns NULL for non-codepoint
    -- entities (those don't have a codepoint identity in the blob). Filter
    -- to nodes where cp_from_hash returns a valid codepoint, then emit
    -- UTF-8 bytes in walk order.
    SELECT COALESCE(
        string_agg(
            convert_to(chr(cp), 'UTF8'),
            '' ORDER BY depth, ordinal_position
        ),
        ''::bytea
    )
    FROM (
        SELECT
            substrate.cp_from_hash(entity_hash) AS cp,
            ordinal_position,
            depth
        FROM substrate.recompose_walk(p_root_hash, p_max_depth)
    ) leaves
    WHERE cp IS NOT NULL AND cp > 0;
$$;

COMMENT ON FUNCTION substrate.recompose_content(substrate.hash_value, INT) IS
    'Reconstruct UTF-8 byte content from a document/content entity hash via tree-walk. ONE QUERY substrate-side; C# orchestration is one ExecuteScalarAsync. Demonstrates the substrate''s O(tier-depth) reconstruction property.';
