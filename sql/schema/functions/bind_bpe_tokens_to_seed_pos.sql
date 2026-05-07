CREATE OR REPLACE FUNCTION substrate.bind_bpe_tokens_to_seed_pos(p_model_source_id INT)
RETURNS BIGINT
LANGUAGE sql VOLATILE AS $f$
    WITH inserted AS (
        INSERT INTO substrate.entity_pos (entity_hash, pos_id)
        SELECT DISTINCT token_member.entity_hash, lemma_pos.pos_id
          FROM substrate.edge coverage
          JOIN substrate.edge_type coverage_type ON coverage_type.id = coverage.edge_type_id
          JOIN substrate.edge_member token_member
            ON token_member.edge_type_id = coverage.edge_type_id
           AND token_member.edge_hash = coverage.hash
          JOIN substrate.edge_role token_role
            ON token_role.id = token_member.edge_role_id
           AND token_role.code = 'source'
          JOIN substrate.edge_member lemma_member
            ON lemma_member.edge_type_id = coverage.edge_type_id
           AND lemma_member.edge_hash = coverage.hash
          JOIN substrate.edge_role lemma_role
            ON lemma_role.id = lemma_member.edge_role_id
           AND lemma_role.code = 'target'
          JOIN substrate.entity_pos lemma_pos ON lemma_pos.entity_hash = lemma_member.entity_hash
          JOIN substrate.entity_model_source model_entity
            ON model_entity.entity_hash = token_member.entity_hash
         WHERE coverage_type.code = 'covers_lemma'
           AND model_entity.model_source_id = p_model_source_id
        ON CONFLICT (entity_hash, pos_id) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM inserted;
$f$;

COMMENT ON FUNCTION substrate.bind_bpe_tokens_to_seed_pos(INT) IS
    'Propagate POS junction evidence from lemma targets to model bpe_token sources over covers_lemma edges.';