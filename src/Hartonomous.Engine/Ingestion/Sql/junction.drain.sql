WITH src AS (SELECT * FROM pg_temp.junction_inflight)
  , ins_pos AS (
        INSERT INTO substrate.entity_pos (entity_hash, pos_id, attestation_type_id, mu)
        SELECT DISTINCT entity_hash, ref_id AS pos_id, attestation_type_id, COALESCE(mu, 1500.0) AS mu
          FROM src WHERE table_name = 'entity_pos'
         ORDER BY entity_hash, pos_id, attestation_type_id, mu DESC
        ON CONFLICT (entity_hash, pos_id, attestation_type_id) DO NOTHING
        RETURNING 1
    )
  , ins_lex AS (
        INSERT INTO substrate.entity_lexname (entity_hash, lexname_id)
        SELECT DISTINCT entity_hash, ref_id AS lexname_id
          FROM src WHERE table_name = 'entity_lexname'
         ORDER BY entity_hash, lexname_id
        ON CONFLICT (entity_hash, lexname_id) DO NOTHING
        RETURNING 1
    )
  , ins_lang AS (
        INSERT INTO substrate.entity_language (entity_hash, language_id)
        SELECT DISTINCT entity_hash, ref_id AS language_id
          FROM src WHERE table_name = 'entity_language'
         ORDER BY entity_hash, language_id
        ON CONFLICT (entity_hash, language_id) DO NOTHING
        RETURNING 1
    )
  , ins_morph AS (
        INSERT INTO substrate.entity_morph_feature (entity_hash, morph_feature_id)
        SELECT DISTINCT entity_hash, ref_id AS morph_feature_id
          FROM src WHERE table_name = 'entity_morph_feature'
         ORDER BY entity_hash, morph_feature_id
        ON CONFLICT (entity_hash, morph_feature_id) DO NOTHING
        RETURNING 1
    )
  , ins_arch AS (
        INSERT INTO substrate.model_architecture_class (entity_hash, architecture_class_id)
        SELECT DISTINCT entity_hash, ref_id AS architecture_class_id
          FROM src WHERE table_name = 'model_architecture_class'
         ORDER BY entity_hash, architecture_class_id
        ON CONFLICT (entity_hash, architecture_class_id) DO NOTHING
        RETURNING 1
    )
  , ins_trole AS (
        INSERT INTO substrate.tensor_tensor_role (entity_hash, tensor_role_id)
        SELECT DISTINCT entity_hash, ref_id AS tensor_role_id
          FROM src WHERE table_name = 'tensor_tensor_role'
         ORDER BY entity_hash, tensor_role_id
        ON CONFLICT (entity_hash, tensor_role_id) DO NOTHING
        RETURNING 1
    )
  , ins_pdep AS (
        INSERT INTO substrate.pattern_deprel (entity_hash, deprel_id, attestation_type_id, mu)
        SELECT DISTINCT entity_hash, ref_id AS deprel_id, attestation_type_id, COALESCE(mu, 1500.0) AS mu
          FROM src WHERE table_name = 'pattern_deprel'
         ORDER BY entity_hash, deprel_id, attestation_type_id, mu DESC
        ON CONFLICT (entity_hash, deprel_id, attestation_type_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_gc AS (
        INSERT INTO substrate.cp_general_category (entity_hash, general_category_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_general_category'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, general_category_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_script AS (
        INSERT INTO substrate.cp_script (entity_hash, script_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_script'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, script_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_block AS (
        INSERT INTO substrate.cp_block (entity_hash, block_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_block'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, block_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_bidi AS (
        INSERT INTO substrate.cp_bidi_class (entity_hash, bidi_class_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_bidi_class'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, bidi_class_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_eaw AS (
        INSERT INTO substrate.cp_east_asian_width (entity_hash, east_asian_width_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_east_asian_width'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, east_asian_width_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_gcb AS (
        INSERT INTO substrate.cp_grapheme_break (entity_hash, break_property_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_grapheme_break'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, break_property_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_wb AS (
        INSERT INTO substrate.cp_word_break (entity_hash, break_property_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_word_break'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, break_property_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_sb AS (
        INSERT INTO substrate.cp_sentence_break (entity_hash, break_property_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_sentence_break'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, break_property_id) DO NOTHING
        RETURNING 1
    )
  , ins_cp_lb AS (
        INSERT INTO substrate.cp_line_break (entity_hash, break_property_id)
        SELECT DISTINCT entity_hash, ref_id FROM src WHERE table_name = 'cp_line_break'
         ORDER BY entity_hash, ref_id
        ON CONFLICT (entity_hash, break_property_id) DO NOTHING
        RETURNING 1
    )
SELECT COUNT(*) FROM (
    SELECT 1 FROM ins_pos UNION ALL
    SELECT 1 FROM ins_lex UNION ALL
    SELECT 1 FROM ins_lang UNION ALL
    SELECT 1 FROM ins_morph UNION ALL
    SELECT 1 FROM ins_arch UNION ALL
    SELECT 1 FROM ins_trole UNION ALL
    SELECT 1 FROM ins_pdep UNION ALL
    SELECT 1 FROM ins_cp_gc UNION ALL
    SELECT 1 FROM ins_cp_script UNION ALL
    SELECT 1 FROM ins_cp_block UNION ALL
    SELECT 1 FROM ins_cp_bidi UNION ALL
    SELECT 1 FROM ins_cp_eaw UNION ALL
    SELECT 1 FROM ins_cp_gcb UNION ALL
    SELECT 1 FROM ins_cp_wb UNION ALL
    SELECT 1 FROM ins_cp_sb UNION ALL
    SELECT 1 FROM ins_cp_lb
) all_ins
