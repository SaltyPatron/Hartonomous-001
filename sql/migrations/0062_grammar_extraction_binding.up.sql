-- 0062_grammar_extraction_binding.up.sql
--
-- Phase A — GrammarExtractionPass support. Per the build-plan task title
-- "GrammarExtractionPass — bind tokenizer tokens to seed POS/deprel/morph",
-- this function propagates seed-lexicon grammatical evidence (POS tags,
-- morphological features) from lemma entities onto the bpe_token entities
-- they cover. The bpe_tokens then "speak" the seed lexicon's taxonomy:
-- a query over `entity_pos` for tokens classified as nouns / verbs / adjs
-- works uniformly across model-derived bpe_tokens and seed-derived lemmas.
--
-- The covers_lemma edges (emitted by VocabCoveragePass) are the bridge
-- between the model's vocabulary and the substrate's lexicon. This
-- function walks them and copies POS / morph_feature junction evidence
-- onto the bpe_token side.
--
-- Glicko-2 ratings on the propagated junctions seed at the same default
-- as fresh entity_pos rows (mu=50000, sigma=11667). Inference-time
-- arena competition resolves whether the propagation is correct for
-- this token.

CREATE OR REPLACE FUNCTION substrate.bind_bpe_tokens_to_seed_pos(
    p_model_source_id BIGINT
) RETURNS BIGINT AS $$
    WITH inserted AS (
        INSERT INTO substrate.entity_pos (entity_id, pos_id)
        SELECT DISTINCT bt_member.entity_id, lp.pos_id
          FROM substrate.edge cov
          JOIN substrate.edge_type cov_et ON cov_et.id = cov.edge_type_id
          JOIN substrate.edge_member bt_member ON bt_member.edge_id = cov.id AND bt_member.edge_role_id = 1
          JOIN substrate.edge_member lemma_member ON lemma_member.edge_id = cov.id AND lemma_member.edge_role_id = 2
          JOIN substrate.entity_pos lp ON lp.entity_id = lemma_member.entity_id
          JOIN substrate.entity_model_source ems ON ems.entity_id = bt_member.entity_id
         WHERE cov_et.code = 'covers_lemma'
           AND ems.model_source_id = p_model_source_id
        ON CONFLICT (entity_id, pos_id) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM inserted;
$$ LANGUAGE SQL;

CREATE OR REPLACE FUNCTION substrate.bind_bpe_tokens_to_seed_morph(
    p_model_source_id BIGINT
) RETURNS BIGINT AS $$
    WITH inserted AS (
        INSERT INTO substrate.entity_morph_feature (entity_id, morph_feature_id)
        SELECT DISTINCT bt_member.entity_id, lm.morph_feature_id
          FROM substrate.edge cov
          JOIN substrate.edge_type cov_et ON cov_et.id = cov.edge_type_id
          JOIN substrate.edge_member bt_member ON bt_member.edge_id = cov.id AND bt_member.edge_role_id = 1
          JOIN substrate.edge_member lemma_member ON lemma_member.edge_id = cov.id AND lemma_member.edge_role_id = 2
          JOIN substrate.entity_morph_feature lm ON lm.entity_id = lemma_member.entity_id
          JOIN substrate.entity_model_source ems ON ems.entity_id = bt_member.entity_id
         WHERE cov_et.code = 'covers_lemma'
           AND ems.model_source_id = p_model_source_id
        ON CONFLICT (entity_id, morph_feature_id) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM inserted;
$$ LANGUAGE SQL;
