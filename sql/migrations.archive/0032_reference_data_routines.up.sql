-- 0032_reference_data_routines.up.sql
-- Reference-data mutation functions used by decomposer-side reference writers.
--
-- The goal is the same pattern already used by model identity and checkpointing:
-- DML logic lives in substrate SQL routines; C# callers pass typed arrays / jsonb and
-- do not compose INSERT/UPDATE statements inline.

CREATE OR REPLACE FUNCTION substrate.upsert_reference_edge_type(
    p_code               text,
    p_category           text,
    p_source_entity_type text,
    p_target_entity_type text
) RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    v_id             int;
    v_source_type_id int;
    v_target_type_id int;
BEGIN
    SELECT id INTO v_id
    FROM substrate.edge_type
    WHERE code = p_code;
    IF FOUND THEN
        RETURN v_id;
    END IF;

    SELECT id INTO v_source_type_id
    FROM substrate.entity_type
    WHERE code = p_source_entity_type;
    IF v_source_type_id IS NULL THEN
        RAISE EXCEPTION 'Unknown source entity_type code: %', p_source_entity_type;
    END IF;

    SELECT id INTO v_target_type_id
    FROM substrate.entity_type
    WHERE code = p_target_entity_type;
    IF v_target_type_id IS NULL THEN
        RAISE EXCEPTION 'Unknown target entity_type code: %', p_target_entity_type;
    END IF;

    INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id)
    VALUES (p_code, p_category, v_source_type_id, v_target_type_id)
    ON CONFLICT (code) DO NOTHING
    RETURNING id INTO v_id;

    IF v_id IS NULL THEN
        SELECT id INTO STRICT v_id
        FROM substrate.edge_type
        WHERE code = p_code;
    END IF;

    RETURN v_id;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.populate_morph_features(
    p_keys   varchar[],
    p_values varchar[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    IF array_length(p_keys, 1) IS DISTINCT FROM array_length(p_values, 1) THEN
        RAISE EXCEPTION 'populate_morph_features: array lengths must match. keys=%, values=%',
            array_length(p_keys, 1),
            array_length(p_values, 1);
    END IF;

    WITH ins AS (
        INSERT INTO substrate.morph_feature (key, value)
        SELECT *
        FROM unnest(p_keys, p_values)
        ON CONFLICT (key, value) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.populate_deprels(
    p_codes varchar[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    WITH ins AS (
        INSERT INTO substrate.deprel (code)
        SELECT DISTINCT *
        FROM unnest(p_codes)
        ON CONFLICT (code) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;

    UPDATE substrate.deprel AS child
    SET parent_id = parent.id
    FROM substrate.deprel AS parent
    WHERE child.parent_id IS NULL
      AND position(':' IN child.code) > 0
      AND parent.code = split_part(child.code, ':', 1);

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.populate_languages(
    p_codes   char(3)[],
    p_names   varchar[],
    p_scopes  char(1)[],
    p_types   char(1)[],
    p_part1s  char(2)[],
    p_part2bs char(3)[],
    p_part2ts char(3)[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    IF array_length(p_codes, 1) IS DISTINCT FROM array_length(p_names, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_scopes, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_types, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_part1s, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_part2bs, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_part2ts, 1) THEN
        RAISE EXCEPTION 'populate_languages: array lengths must match';
    END IF;

    WITH upserted AS (
        INSERT INTO substrate.language (code, name, scope, type, part1, part2b, part2t)
        SELECT *
        FROM unnest(p_codes, p_names, p_scopes, p_types, p_part1s, p_part2bs, p_part2ts)
        ON CONFLICT (code) DO UPDATE
        SET part1 = EXCLUDED.part1,
            part2b = EXCLUDED.part2b,
            part2t = EXCLUDED.part2t
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM upserted;

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.update_language_name_entity_ids(
    p_codes      char(3)[],
    p_entity_ids bigint[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    IF array_length(p_codes, 1) IS DISTINCT FROM array_length(p_entity_ids, 1) THEN
        RAISE EXCEPTION 'update_language_name_entity_ids: array lengths must match. codes=%, entity_ids=%',
            array_length(p_codes, 1),
            array_length(p_entity_ids, 1);
    END IF;

    UPDATE substrate.language
    SET name_entity_id = t.eid
    FROM unnest(p_codes, p_entity_ids) AS t(c, eid)
    WHERE language.code = t.c;

    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.upsert_homogeneous_edge_types(
    p_codes            varchar[],
    p_category         varchar,
    p_entity_type_code text
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_entity_type_id int;
    v_rows bigint;
BEGIN
    SELECT id INTO v_entity_type_id
    FROM substrate.entity_type
    WHERE code = p_entity_type_code;
    IF v_entity_type_id IS NULL THEN
        RAISE EXCEPTION 'Unknown entity_type code: %', p_entity_type_code;
    END IF;

    WITH ins AS (
        INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id)
        SELECT code, p_category, v_entity_type_id, v_entity_type_id
        FROM unnest(p_codes) AS t(code)
        ON CONFLICT (code) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.populate_senses(
    p_codes       varchar[],
    p_glosses     text[],
    p_lexname_ids int[],
    p_pos_ids     int[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    IF array_length(p_codes, 1) IS DISTINCT FROM array_length(p_glosses, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_lexname_ids, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_pos_ids, 1) THEN
        RAISE EXCEPTION 'populate_senses: array lengths must match';
    END IF;

    WITH ins AS (
        INSERT INTO substrate.sense (code, gloss, lexname_id, pos_id)
        SELECT *
        FROM unnest(p_codes, p_glosses, p_lexname_ids, p_pos_ids)
        ON CONFLICT (code) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.populate_general_categories(
    p_codes        text[],
    p_group_codes  text[],
    p_descriptions text[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    IF array_length(p_codes, 1) IS DISTINCT FROM array_length(p_group_codes, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_descriptions, 1) THEN
        RAISE EXCEPTION 'populate_general_categories: array lengths must match';
    END IF;

    WITH ins AS (
        INSERT INTO substrate.general_category (code, group_code, description)
        SELECT *
        FROM unnest(p_codes, p_group_codes, p_descriptions)
        ON CONFLICT (code) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.populate_scripts(
    p_codes text[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    WITH ins AS (
        INSERT INTO substrate.script (code)
        SELECT *
        FROM unnest(p_codes)
        ON CONFLICT (code) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.populate_blocks(
    p_codes        text[],
    p_range_starts int[],
    p_range_ends   int[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    IF array_length(p_codes, 1) IS DISTINCT FROM array_length(p_range_starts, 1)
       OR array_length(p_codes, 1) IS DISTINCT FROM array_length(p_range_ends, 1) THEN
        RAISE EXCEPTION 'populate_blocks: array lengths must match';
    END IF;

    WITH ins AS (
        INSERT INTO substrate.block (code, range_start, range_end)
        SELECT *
        FROM unnest(p_codes, p_range_starts, p_range_ends)
        ON CONFLICT (code) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.populate_break_properties(
    p_codes      text[],
    p_categories text[]
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    IF array_length(p_codes, 1) IS DISTINCT FROM array_length(p_categories, 1) THEN
        RAISE EXCEPTION 'populate_break_properties: array lengths must match';
    END IF;

    WITH ins AS (
        INSERT INTO substrate.break_property (code, category)
        SELECT *
        FROM unnest(p_codes, p_categories)
        ON CONFLICT (code, category) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;

    RETURN v_rows;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.write_codepoint_properties_json(
    p_rows jsonb
) RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows bigint;
BEGIN
    IF p_rows IS NULL OR jsonb_typeof(p_rows) <> 'array' OR jsonb_array_length(p_rows) = 0 THEN
        RETURN 0;
    END IF;

    INSERT INTO substrate.codepoint_property (
        entity_id,
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
        full_case_fold)
    SELECT
        entity_id,
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
    FROM jsonb_to_recordset(p_rows) AS r(
        entity_id bigint,
        codepoint_value int,
        general_category_id int,
        script_id int,
        block_id int,
        gcb_id int,
        wb_id int,
        sb_id int,
        lb_id int,
        is_extended_pictographic boolean,
        ccc smallint,
        decomposition_type text,
        decomposition_mapping int[],
        simple_case_fold int,
        full_case_fold int[]);

    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END;
$$;

COMMENT ON FUNCTION substrate.upsert_reference_edge_type(text, text, text, text) IS
    'SQL-first edge_type upsert used by reference writers; callers pass codes, not inline INSERT statements.';
COMMENT ON FUNCTION substrate.populate_morph_features(varchar[], varchar[]) IS
    'Bulk inserts morph_feature rows from parallel key/value arrays.';
COMMENT ON FUNCTION substrate.populate_deprels(varchar[]) IS
    'Bulk inserts deprel rows and resolves parent links for subtype relations like acl:relcl.';
COMMENT ON FUNCTION substrate.populate_languages(char(3)[], varchar[], char(1)[], char(1)[], char(2)[], char(3)[], char(3)[]) IS
    'Bulk upserts ISO 639 language rows from typed parallel arrays.';
COMMENT ON FUNCTION substrate.update_language_name_entity_ids(char(3)[], bigint[]) IS
    'Bulk back-fills language.name_entity_id from language code to language_name entity id mappings.';
COMMENT ON FUNCTION substrate.upsert_homogeneous_edge_types(varchar[], varchar, text) IS
    'Bulk upserts edge types where source_type_id and target_type_id are the same entity_type.';
COMMENT ON FUNCTION substrate.populate_senses(varchar[], text[], int[], int[]) IS
    'Bulk inserts WordNet sense rows.';
COMMENT ON FUNCTION substrate.populate_general_categories(text[], text[], text[]) IS
    'Bulk inserts Unicode general_category rows.';
COMMENT ON FUNCTION substrate.populate_scripts(text[]) IS
    'Bulk inserts Unicode script rows.';
COMMENT ON FUNCTION substrate.populate_blocks(text[], int[], int[]) IS
    'Bulk inserts Unicode block rows.';
COMMENT ON FUNCTION substrate.populate_break_properties(text[], text[]) IS
    'Bulk inserts Unicode break_property rows.';
COMMENT ON FUNCTION substrate.write_codepoint_properties_json(jsonb) IS
    'Bulk inserts codepoint_property rows from a jsonb array payload; keeps row-shape semantics in SQL instead of inline C# COPY statements.';
