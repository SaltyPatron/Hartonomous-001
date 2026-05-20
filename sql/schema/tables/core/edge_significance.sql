-- Glicko-2 ratings on edges, per arena, per attestation_type. Edge cost
-- during A* traversal = 1 / blended_mu where blended_mu is computed at
-- query time from per-attestation_type rows under an AttestationTypeBlend
-- recipe (default: equal weight across attestation_types within arena).
--
-- New arenas (open vocabulary) get inline priors at edge-emit for every
-- edge inserted after the arena is created; the pipeline reloads the
-- (provenance × edge_type × arena) primer table at its next startup.
-- AP-37: no end-of-phase post-pass.
--
-- attestation_type_id stratifies the rating: same edge gets separate rows
-- per (arena, attestation_type) so corpus-window evidence, model-circuit
-- evidence, lexicon-curated evidence, and inference-outcome evidence remain
-- distinguishable. The recomposer's WHERE clause and the inference engine's
-- traversal blend can both filter by attestation_type to pull
-- circuit-only-students, lexicon-only-students, etc.
--
-- Partitioned by HASH(edge_hash) MODULUS 8 (architectural debt #6, 2026-05-19).
-- Prior LIST(context_type_id) partitioning concentrated bulk-ingest writes onto
-- the single partition for each arena, causing LWLock BufferContent contention
-- across the parallel worker pool. Hash-by-edge_hash distributes writes for
-- different edges across all 8 partitions independently of arena, eliminating
-- the cross-worker buffer contention while preserving per-(arena, edge) PK
-- lookup efficiency.
CREATE TABLE substrate.edge_significance (
    context_type_id     INT NOT NULL REFERENCES substrate.significance_context(id),
    edge_type_id        INT NOT NULL,
    edge_hash           substrate.hash_value NOT NULL,
    attestation_type_id INT NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma               substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility          substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (edge_hash, context_type_id, edge_type_id, attestation_type_id)
    -- FK to substrate.edge application-enforced.
) PARTITION BY HASH (edge_hash);

COMMENT ON TABLE substrate.edge_significance IS
    'Glicko-2 trust per (edge, arena, attestation_type). Hash-addressable via (edge_type_id, edge_hash). Hash-partitioned by edge_hash (modulus 8) for write-path concurrency; query plans use the (edge_hash, ...) PK btree for point and range lookups. Stratified by attestation_type so kinds of evidence (corpus, model, lexicon, outcome) remain distinguishable; query-time AttestationTypeBlend collapses them.';
