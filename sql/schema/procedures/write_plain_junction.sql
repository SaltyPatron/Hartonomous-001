CREATE OR REPLACE PROCEDURE substrate.write_plain_junction(
    p_table_name TEXT,
    p_ref_column TEXT,
    p_entity_hashes BYTEA[],
    p_ref_ids INT[]
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT := lower(CASE WHEN left(p_table_name, 10) = 'substrate.' THEN substring(p_table_name FROM 11) ELSE p_table_name END);
    v_ref_column TEXT := lower(p_ref_column);
BEGIN
    IF p_entity_hashes IS NULL OR p_ref_ids IS NULL THEN
        RAISE EXCEPTION 'Junction arrays cannot be null';
    END IF;

    IF cardinality(p_entity_hashes) <> cardinality(p_ref_ids) THEN
        RAISE EXCEPTION 'Junction array lengths must match: hashes %, refs %',
            cardinality(p_entity_hashes), cardinality(p_ref_ids);
    END IF;

    IF v_table_name = 'entity_language' AND v_ref_column = 'language_id' THEN
        INSERT INTO substrate.entity_language (entity_hash, language_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, language_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'entity_morph_feature' AND v_ref_column = 'morph_feature_id' THEN
        INSERT INTO substrate.entity_morph_feature (entity_hash, morph_feature_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, morph_feature_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'entity_lexname' AND v_ref_column = 'lexname_id' THEN
        INSERT INTO substrate.entity_lexname (entity_hash, lexname_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, lexname_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'model_architecture_class' AND v_ref_column = 'architecture_class_id' THEN
        INSERT INTO substrate.model_architecture_class (entity_hash, architecture_class_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, architecture_class_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'tensor_tensor_role' AND v_ref_column = 'tensor_role_id' THEN
        INSERT INTO substrate.tensor_tensor_role (entity_hash, tensor_role_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, tensor_role_id) DO NOTHING;
        RETURN;
    END IF;

    RAISE EXCEPTION 'Unsupported plain junction target %.%', v_table_name, v_ref_column;
END $$;

COMMENT ON PROCEDURE substrate.write_plain_junction(TEXT, TEXT, BYTEA[], INT[]) IS
    'Bulk insert allowlisted plain junction rows. Routing is SQL-side and explicit.';
