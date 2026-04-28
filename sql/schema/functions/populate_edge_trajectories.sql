CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(p_limit INT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated BIGINT := 0;
BEGIN
    -- STUB: returns 0, no edge geometry populated. The proper implementation
    -- (task #53, #55) builds per-edge-type LINESTRINGZM trajectories that
    -- meaningfully differentiate edges of the same type — a flat
    -- ST_MakeLine(source_centroid, target_centroid) would make every binary
    -- edge of the same type-pair geometrically identical, breaking Fréchet,
    -- Hausdorff, similar_edges, frayed_edges, and analogy completion.
    -- Returning 0 means "no work done" and the C# loop terminates.
    PERFORM p_limit;
    RETURN v_updated;
END $$;
COMMENT ON FUNCTION substrate.populate_edge_trajectories(INT) IS
    'STUB pending differentiated per-edge-type trajectory builders (rules/25-physicality-4d.md, task #53 + #55).';
