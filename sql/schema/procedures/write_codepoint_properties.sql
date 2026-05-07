CREATE OR REPLACE PROCEDURE substrate.write_codepoint_properties(p_rows JSONB)
LANGUAGE plpgsql
AS $$
BEGIN
    IF p_rows IS NULL OR jsonb_typeof(p_rows) <> 'array' THEN
        RAISE EXCEPTION 'Codepoint property payload must be a JSON array';
    END IF;

    INSERT INTO substrate.codepoint_property (
        entity_hash,
        codepoint_value,
        general_category_id,
        script_id,
        block_id,
        gcb_id,
        wb_id,
        sb_id,
        lb_id,
        is_extended_pictographic,
        ccc,
        decomposition_type,
        decomposition_mapping,
        simple_case_fold,
        full_case_fold
    )
    SELECT
        decode(src.entity_hash_hex, 'hex')::substrate.hash_value,
        src.codepoint_value,
        src.general_category_id,
        src.script_id,
        src.block_id,
        src.gcb_id,
        src.wb_id,
        src.sb_id,
        src.lb_id,
        src.is_extended_pictographic,
        src.ccc,
        src.decomposition_type,
        src.decomposition_mapping,
        src.simple_case_fold,
        src.full_case_fold
      FROM jsonb_to_recordset(p_rows) AS src(
        entity_hash_hex TEXT,
        codepoint_value INT,
        general_category_id INT,
        script_id INT,
        block_id INT,
        gcb_id INT,
        wb_id INT,
        sb_id INT,
        lb_id INT,
        is_extended_pictographic BOOLEAN,
        ccc SMALLINT,
        decomposition_type VARCHAR(16),
        decomposition_mapping INT[],
        simple_case_fold INT,
        full_case_fold INT[]
      )
    ON CONFLICT (entity_hash) DO NOTHING;
END $$;

COMMENT ON PROCEDURE substrate.write_codepoint_properties(JSONB) IS
    'Bulk insert codepoint_property rows from a JSONB recordset payload, preserving idempotent ON CONFLICT behavior.';