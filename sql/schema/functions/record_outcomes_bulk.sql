CREATE OR REPLACE FUNCTION substrate.record_outcomes_bulk(
    p_winner_target_hashes BYTEA[],
    p_winner_group_ids     INT[],
    p_loser_target_hashes  BYTEA[],
    p_loser_group_ids      INT[],
    p_attestation_type_code TEXT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_attestation_type_id INT;
    v_events INT;
BEGIN
    IF p_winner_target_hashes IS NULL
       OR p_winner_group_ids IS NULL
       OR p_loser_target_hashes IS NULL
       OR p_loser_group_ids IS NULL THEN
        RETURN 0;
    END IF;

    SELECT id
      INTO v_attestation_type_id
      FROM substrate.attestation_type
     WHERE code = p_attestation_type_code;

    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type code: %', p_attestation_type_code;
    END IF;

    WITH winner_groups AS (
        SELECT winner_hash, group_id
        FROM unnest(p_winner_target_hashes, p_winner_group_ids) AS w(winner_hash, group_id)
        WHERE winner_hash IS NOT NULL
    ),
    loser_groups AS (
        SELECT group_id, array_agg(loser_hash) AS loser_hashes
        FROM unnest(p_loser_target_hashes, p_loser_group_ids) AS l(loser_hash, group_id)
        WHERE loser_hash IS NOT NULL
        GROUP BY group_id
    ),
    outcome_calls AS (
        SELECT substrate.record_outcome(
                   sc.id,
                   wg.winner_hash,
                   lg.loser_hashes,
                   v_attestation_type_id) AS events
        FROM winner_groups AS wg
        JOIN loser_groups AS lg USING (group_id)
        CROSS JOIN substrate.significance_context AS sc
    )
    SELECT COALESCE(SUM(events), 0)::INT
      INTO v_events
      FROM outcome_calls;

    RETURN v_events;
END $$;

COMMENT ON FUNCTION substrate.record_outcomes_bulk(BYTEA[], INT[], BYTEA[], INT[], TEXT) IS
    'Bulk Step-6 outcome recorder. C# sends flattened winner/loser groups once; SQL fans out across all significance contexts and delegates each grouped comparison to substrate.record_outcome, which performs set-based edge selection and native bulk-Glicko updates.';
