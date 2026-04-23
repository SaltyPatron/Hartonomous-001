-- 0033_reference_data_read_routines.up.sql
-- Allowlisted reference-data read functions. These replace generic table-name
-- interpolation in the C# reference reader with stable substrate functions.

CREATE OR REPLACE FUNCTION substrate.reference_code_map(
    p_table_name text
) RETURNS TABLE (id int, code text)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    CASE p_table_name
        WHEN 'substrate.entity_type' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.entity_type t;
        WHEN 'substrate.edge_type' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.edge_type t;
        WHEN 'substrate.physicality_type' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.physicality_type t;
        WHEN 'substrate.significance_context' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.significance_context t;
        WHEN 'substrate.provenance' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.provenance t;
        WHEN 'substrate.edge_role' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.edge_role t;
        WHEN 'substrate.language' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.language t;
        WHEN 'substrate.pos' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.pos t;
        WHEN 'substrate.general_category' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.general_category t;
        WHEN 'substrate.script' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.script t;
        WHEN 'substrate.block' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.block t;
        WHEN 'substrate.deprel' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.deprel t;
        WHEN 'substrate.tensor_role' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.tensor_role t;
        WHEN 'substrate.lexname' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.lexname t;
        WHEN 'substrate.sense' THEN
            RETURN QUERY SELECT t.id, t.code::text FROM substrate.sense t;
        ELSE
            RAISE EXCEPTION 'reference_code_map: unsupported table %', p_table_name;
    END CASE;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.reference_key_value_map(
    p_table_name   text,
    p_key_column   text,
    p_value_column text
) RETURNS TABLE (id int, key_text text, value_text text)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table_name = 'substrate.morph_feature' AND p_key_column = 'key' AND p_value_column = 'value' THEN
        RETURN QUERY
        SELECT t.id, t.key::text, t.value::text
        FROM substrate.morph_feature t;
        RETURN;
    END IF;

    IF p_table_name = 'substrate.break_property' AND p_key_column = 'code' AND p_value_column = 'category' THEN
        RETURN QUERY
        SELECT t.id, t.code::text, t.category::text
        FROM substrate.break_property t;
        RETURN;
    END IF;

    RAISE EXCEPTION 'reference_key_value_map: unsupported shape %, %, %',
        p_table_name, p_key_column, p_value_column;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.reference_code_text_map(
    p_table_name   text,
    p_value_column text
) RETURNS TABLE (code text, value_text text)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table_name = 'substrate.sense' AND p_value_column = 'gloss' THEN
        RETURN QUERY
        SELECT t.code::text, t.gloss::text
        FROM substrate.sense t;
        RETURN;
    END IF;

    RAISE EXCEPTION 'reference_code_text_map: unsupported shape %.%', p_table_name, p_value_column;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.reference_int64_set(
    p_table_name  text,
    p_column_name text
) RETURNS TABLE (value bigint)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table_name = 'substrate.codepoint_property' AND p_column_name = 'entity_id' THEN
        RETURN QUERY
        SELECT t.entity_id
        FROM substrate.codepoint_property t;
        RETURN;
    END IF;

    RAISE EXCEPTION 'reference_int64_set: unsupported shape %.%', p_table_name, p_column_name;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.reference_id_by_code(
    p_table_name text,
    p_code       text
) RETURNS int
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_id int;
BEGIN
    CASE p_table_name
        WHEN 'substrate.language' THEN
            SELECT t.id INTO v_id FROM substrate.language t WHERE t.code = p_code;
        WHEN 'substrate.entity_type' THEN
            SELECT t.id INTO v_id FROM substrate.entity_type t WHERE t.code = p_code;
        WHEN 'substrate.edge_type' THEN
            SELECT t.id INTO v_id FROM substrate.edge_type t WHERE t.code = p_code;
        WHEN 'substrate.physicality_type' THEN
            SELECT t.id INTO v_id FROM substrate.physicality_type t WHERE t.code = p_code;
        WHEN 'substrate.significance_context' THEN
            SELECT t.id INTO v_id FROM substrate.significance_context t WHERE t.code = p_code;
        WHEN 'substrate.provenance' THEN
            SELECT t.id INTO v_id FROM substrate.provenance t WHERE t.code = p_code;
        WHEN 'substrate.edge_role' THEN
            SELECT t.id INTO v_id FROM substrate.edge_role t WHERE t.code = p_code;
        WHEN 'substrate.pos' THEN
            SELECT t.id INTO v_id FROM substrate.pos t WHERE t.code = p_code;
        WHEN 'substrate.general_category' THEN
            SELECT t.id INTO v_id FROM substrate.general_category t WHERE t.code = p_code;
        WHEN 'substrate.script' THEN
            SELECT t.id INTO v_id FROM substrate.script t WHERE t.code = p_code;
        WHEN 'substrate.block' THEN
            SELECT t.id INTO v_id FROM substrate.block t WHERE t.code = p_code;
        WHEN 'substrate.deprel' THEN
            SELECT t.id INTO v_id FROM substrate.deprel t WHERE t.code = p_code;
        WHEN 'substrate.tensor_role' THEN
            SELECT t.id INTO v_id FROM substrate.tensor_role t WHERE t.code = p_code;
        WHEN 'substrate.lexname' THEN
            SELECT t.id INTO v_id FROM substrate.lexname t WHERE t.code = p_code;
        WHEN 'substrate.sense' THEN
            SELECT t.id INTO v_id FROM substrate.sense t WHERE t.code = p_code;
        ELSE
            RAISE EXCEPTION 'reference_id_by_code: unsupported table %', p_table_name;
    END CASE;

    IF v_id IS NULL THEN
        RAISE EXCEPTION 'reference_id_by_code: code % not found in %', p_code, p_table_name;
    END IF;

    RETURN v_id;
END;
$$;

COMMENT ON FUNCTION substrate.reference_code_map(text) IS
    'Allowlisted reference-table code->id reader used by the C# reference data reader.';
COMMENT ON FUNCTION substrate.reference_key_value_map(text, text, text) IS
    'Allowlisted reference-table (key,value)->id reader for morph_feature and break_property.';
COMMENT ON FUNCTION substrate.reference_code_text_map(text, text) IS
    'Allowlisted reference-table code->text reader.';
COMMENT ON FUNCTION substrate.reference_int64_set(text, text) IS
    'Allowlisted bigint-column reader for reference/junction tables.';
COMMENT ON FUNCTION substrate.reference_id_by_code(text, text) IS
    'Allowlisted single-id lookup by code.';
