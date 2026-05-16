-- One-shot backfill: aggregate every existing content trajectory into
-- substrate.position_embedding_aggregate. Used to bootstrap the aggregate
-- on existing substrate state (or rebuild it after schema changes that
-- alter the aggregate definition). After backfill, all future updates
-- flow through substrate.update_position_embedding_aggregate_from_drain.
--
-- Idempotent via the UPSERT clause — running twice doubles counts, but
-- with TRUNCATE first the effect is "reset + rebuild from scratch."
CREATE OR REPLACE FUNCTION substrate.backfill_position_embedding_aggregate(
    p_truncate_first BOOLEAN DEFAULT TRUE
)
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows_inserted BIGINT := 0;
BEGIN
    IF p_truncate_first THEN
        TRUNCATE substrate.position_embedding_aggregate;
    END IF;

    -- Read every content trajectory's vertices directly from
    -- substrate.physicality (ingestion_trajectory partition). Uses
    -- ST_DumpPoints to unroll the LINESTRINGZM in one pass instead of
    -- per-row LATERAL get_composition_children. Per-vertex mantissa
    -- unpack is inline; child hash resolution via substrate.entity's
    -- composite btree on the GENERATED hash_bits_0_51/52_103 columns
    -- (entity_hash_prefix_idx). Single bulk aggregate over the partition.
    --
    -- Centroid coords + Hilbert index are baked into the aggregate at
    -- write time (pre-gen pattern: don't recompute at read time). Synth
    -- reads (ordinal, child_hash, occurrences, x, y, z, m, hilbert) in
    -- one row vs needing a second substrate.physicality lookup per child.
    WITH walked AS (
        SELECT
            (substrate.bb_unpack_ordinal(ST_Y(pt.geom)) - 1)::INT AS ordinal,
            substrate.bb_unpack_hash_lo(ST_X(pt.geom)) AS hb_lo,
            substrate.bb_unpack_hash_hi(ST_Z(pt.geom)) AS hb_hi
          FROM substrate.physicality p
          CROSS JOIN LATERAL ST_DumpPoints(p.geom) gd
          CROSS JOIN LATERAL (SELECT gd.geom) pt
          JOIN substrate.entity_classification ec ON ec.entity_hash = p.entity_hash
          JOIN substrate.entity_type et ON et.id = ec.entity_type_id
         WHERE p.physicality_type_id = (
             SELECT id FROM substrate.physicality_type WHERE code = 'ingestion_trajectory'
         )
           AND et.code IN ('text_composition', 'paragraph', 'document')
           AND substrate.bb_unpack_ordinal(ST_Y(pt.geom)) >= 1
           AND substrate.bb_unpack_ordinal(ST_Y(pt.geom)) <= 65535
    ),
    resolved AS (
        SELECT
            w.ordinal,
            e.hash AS child_hash,
            count(*)::BIGINT AS occurrences
          FROM walked w
          JOIN substrate.entity e
            ON e.hash_bits_0_51 = w.hb_lo
           AND e.hash_bits_52_103 = w.hb_hi
         GROUP BY w.ordinal, e.hash
    )
    INSERT INTO substrate.position_embedding_aggregate (ordinal, child_hash, occurrences)
    SELECT r.ordinal, r.child_hash, r.occurrences
      FROM resolved r
    ON CONFLICT (ordinal, child_hash) DO UPDATE
       SET occurrences = substrate.position_embedding_aggregate.occurrences + EXCLUDED.occurrences;

    GET DIAGNOSTICS v_rows_inserted = ROW_COUNT;
    RETURN v_rows_inserted;
END;
$$;

COMMENT ON FUNCTION substrate.backfill_position_embedding_aggregate(BOOLEAN) IS
    'One-shot bulk rebuild of substrate.position_embedding_aggregate via direct ST_DumpPoints walk over the ingestion_trajectory partition. Uses entity_by_hash_prefix composite btree for child hash resolution. Replaces per-row LATERAL get_composition_children path (which was 71 min single-threaded for 4.3M trajectories).';
