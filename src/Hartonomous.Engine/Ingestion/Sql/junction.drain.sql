WITH src AS (SELECT * FROM pg_temp.junction_inflight)
  , ins_pos AS (
        INSERT INTO substrate.entity_pos (entity_hash, pos_id, attestation_type_id, mu)
        SELECT DISTINCT entity_hash, ref_id, attestation_type_id, COALESCE(mu, 1500.0)
          FROM src WHERE table_name = 'entity_pos'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
  , ins_lex AS (
        INSERT INTO substrate.entity_lexname (entity_hash, lexname_id)
        SELECT DISTINCT entity_hash, ref_id
          FROM src WHERE table_name = 'entity_lexname'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
  , ins_lang AS (
        INSERT INTO substrate.entity_language (entity_hash, language_id)
        SELECT DISTINCT entity_hash, ref_id
          FROM src WHERE table_name = 'entity_language'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
  , ins_morph AS (
        INSERT INTO substrate.entity_morph_feature (entity_hash, morph_feature_id)
        SELECT DISTINCT entity_hash, ref_id
          FROM src WHERE table_name = 'entity_morph_feature'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
  , ins_arch AS (
        INSERT INTO substrate.model_architecture_class (entity_hash, architecture_class_id)
        SELECT DISTINCT entity_hash, ref_id
          FROM src WHERE table_name = 'model_architecture_class'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
  , ins_trole AS (
        INSERT INTO substrate.tensor_tensor_role (entity_hash, tensor_role_id)
        SELECT DISTINCT entity_hash, ref_id
          FROM src WHERE table_name = 'tensor_tensor_role'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
  , ins_pdep AS (
        INSERT INTO substrate.pattern_deprel (entity_hash, deprel_id, attestation_type_id, mu)
        SELECT DISTINCT entity_hash, ref_id, attestation_type_id, COALESCE(mu, 1500.0)
          FROM src WHERE table_name = 'pattern_deprel'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
SELECT COUNT(*) FROM (
    SELECT 1 FROM ins_pos UNION ALL
    SELECT 1 FROM ins_lex UNION ALL
    SELECT 1 FROM ins_lang UNION ALL
    SELECT 1 FROM ins_morph UNION ALL
    SELECT 1 FROM ins_arch UNION ALL
    SELECT 1 FROM ins_trole UNION ALL
    SELECT 1 FROM ins_pdep
) all_ins
