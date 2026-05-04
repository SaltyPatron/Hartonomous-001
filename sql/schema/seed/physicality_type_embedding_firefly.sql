-- V1 stage 0035 — physicality type extensions.
--
-- KEEP: embedding_firefly. The existing EmbeddingFireflyPass calls
-- AddPhysicalityPoint4d(token_entity, "embedding_firefly", ...) and that
-- physicality_type was missing from the seed, leaving every firefly
-- insert dangling on a non-existent type_id. This is the load-bearing
-- addition.
--
-- REMOVED: firefly_consensus_traj, embedding_native, firefly_at_*_tier.
-- None are emitted by any pass. Adding them registers vocabulary the
-- substrate doesn't use. Bring them back when the matching pass exists.

INSERT INTO substrate.physicality_type (code) VALUES
    ('embedding_firefly')
ON CONFLICT (code) DO NOTHING;
