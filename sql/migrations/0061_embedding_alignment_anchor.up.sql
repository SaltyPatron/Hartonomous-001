-- 0061_embedding_alignment_anchor.up.sql
--
-- Phase C2 of the build plan: cross-model embedding alignment via
-- orthogonal Procrustes. The first ingested model (with sufficient
-- vocab) becomes the canonical anchor; every subsequent model's
-- embedding fireflies get rotated into the anchor's frame so that
-- "king" from Llama and "king" from Qwen converge in 4D space.
--
-- Without this step, the per-model Laplacian eigenmap output is
-- arbitrary up to rotation+reflection — two models' fireflies for the
-- same shared bpe_token sit in independent eigenspaces and never meet,
-- so cross-model Voronoi consensus over the shared bpe_token entity
-- is ill-defined.

CREATE TABLE IF NOT EXISTS substrate.embedding_alignment_anchor (
    model_source_id BIGINT PRIMARY KEY REFERENCES substrate.model_source(id),
    vocab_intersection_token_count INT NOT NULL,
    set_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE substrate.embedding_alignment_anchor IS
    'The single canonical model whose firefly frame all other models align to via Procrustes. '
    'First-write-wins: the first model with sufficient vocab intersection becomes the anchor; '
    'every subsequent EmbeddingAlignmentPass run rotates against this anchor.';

-- Atomic anchor selection: returns the existing anchor if any, else
-- claims the supplied model as the anchor. Caller compares the returned
-- model_source_id with its own to decide whether it IS the anchor.
CREATE OR REPLACE FUNCTION substrate.claim_or_get_embedding_anchor(
    p_model_source_id BIGINT,
    p_intersection_count INT
) RETURNS BIGINT AS $$
    INSERT INTO substrate.embedding_alignment_anchor
        (model_source_id, vocab_intersection_token_count)
    VALUES
        (p_model_source_id, p_intersection_count)
    ON CONFLICT (model_source_id) DO NOTHING;

    SELECT model_source_id FROM substrate.embedding_alignment_anchor
    ORDER BY set_at ASC
    LIMIT 1;
$$ LANGUAGE SQL;

-- Rotates every embedding_firefly POINTZM physicality of a given
-- model_source by a 3x3 orthogonal matrix R, leaving the M coordinate
-- (L2 magnitude) untouched. Run after EmbeddingFireflyPass for non-
-- anchor models. R must be orthogonal (det = +1); the caller is
-- responsible for ensuring this — Procrustes (Kabsch) returns such an R.
CREATE OR REPLACE FUNCTION substrate.apply_firefly_rotation(
    p_model_source_id BIGINT,
    p_r00 FLOAT8, p_r01 FLOAT8, p_r02 FLOAT8,
    p_r10 FLOAT8, p_r11 FLOAT8, p_r12 FLOAT8,
    p_r20 FLOAT8, p_r21 FLOAT8, p_r22 FLOAT8
) RETURNS BIGINT AS $$
    WITH updated AS (
        UPDATE substrate.physicality p
           SET geom = ST_MakePoint(
               p_r00 * ST_X(p.geom) + p_r01 * ST_Y(p.geom) + p_r02 * ST_Z(p.geom),
               p_r10 * ST_X(p.geom) + p_r11 * ST_Y(p.geom) + p_r12 * ST_Z(p.geom),
               p_r20 * ST_X(p.geom) + p_r21 * ST_Y(p.geom) + p_r22 * ST_Z(p.geom),
               ST_M(p.geom))
          FROM substrate.entity_model_source ems,
               substrate.physicality_type pt
         WHERE p.entity_id = ems.entity_id
           AND ems.model_source_id = p_model_source_id
           AND p.physicality_type_id = pt.id
           AND pt.code = 'embedding_firefly'
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM updated;
$$ LANGUAGE SQL;

-- Convenience: extract an anchor's firefly coordinates for a vocab
-- intersection set, returning a row per (entity_id, x, y, z) suitable
-- for streaming back to the C# pass that calls Procrustes. Filtering
-- by entity_id ANY ($1) is both cross-model-deterministic (same vocab
-- → same shared bpe_token entity ids) and small enough to pull into
-- managed memory.
CREATE OR REPLACE FUNCTION substrate.get_firefly_coords(
    p_bpe_token_entity_ids BIGINT[],
    p_model_source_id BIGINT
) RETURNS TABLE(entity_id BIGINT, x FLOAT8, y FLOAT8, z FLOAT8) AS $$
    SELECT p.entity_id, ST_X(p.geom), ST_Y(p.geom), ST_Z(p.geom)
      FROM substrate.physicality p
      JOIN substrate.entity_model_source ems ON ems.entity_id = p.entity_id
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE p.entity_id = ANY(p_bpe_token_entity_ids)
       AND ems.model_source_id = p_model_source_id
       AND pt.code = 'embedding_firefly'
     ORDER BY p.entity_id ASC;
$$ LANGUAGE SQL STABLE;
