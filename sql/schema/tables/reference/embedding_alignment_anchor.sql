-- substrate.embedding_alignment_anchor
--
-- Phase C2 cross-model embedding alignment via orthogonal Procrustes
-- (EmbeddingAlignmentPass). Per-model Laplacian eigenmaps produce firefly
-- coordinates that are arbitrary up to rotation+reflection. Without
-- alignment, two models' fireflies for the same shared bpe_token sit in
-- independent eigenspaces and never converge — Voronoi consensus over the
-- shared entity is ill-defined.
--
-- This table tracks the canonical anchor: the first ingested model with
-- sufficient vocab becomes the anchor; every subsequent model is rotated
-- into the anchor's frame via Kabsch/Procrustes. First-write-wins via
-- ON CONFLICT DO NOTHING in substrate.claim_or_get_embedding_anchor.

CREATE TABLE IF NOT EXISTS substrate.embedding_alignment_anchor (
    model_source_id INT PRIMARY KEY REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    vocab_intersection_token_count INT NOT NULL,
    set_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE substrate.embedding_alignment_anchor IS
    'The single canonical model whose firefly frame all other models align to via Procrustes. First-write-wins: the first model with sufficient vocab intersection becomes the anchor; every subsequent EmbeddingAlignmentPass run rotates against this anchor. Phase C2.';
