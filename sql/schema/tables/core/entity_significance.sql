-- Glicko-2 ratings on entities, per arena, per attestation_type. Hash-only
-- entity reference (Phase C of unification refactor — substrate.entity has
-- hash-only PK, no entity_type_id).
--
-- attestation_type_id partitions the rating surface so corpus-derived,
-- model-derived, lexicon-curated, and inference-outcome evidence stay
-- distinguishable in their contribution to the same (arena, entity) rating.
-- Same content from corpus_co_occurrence_window AND lexical_curated_relation
-- gets two separate rows; the inference engine and recomposer can blend
-- them at query time per AttestationTypeBlend.
--
-- Partitioned by HASH(entity_hash) MODULUS 8 (architectural debt #6,
-- 2026-05-19). Prior LIST(context_type_id) partitioning concentrated
-- bulk-ingest writes onto the single partition for each arena, causing LWLock
-- BufferContent contention across the parallel worker pool. Hash-by-entity_hash
-- distributes writes for different entities across all 8 partitions
-- independently of arena.
CREATE TABLE substrate.entity_significance (
    context_type_id     INT NOT NULL REFERENCES substrate.significance_context(id),
    entity_hash         substrate.hash_value NOT NULL,
    attestation_type_id INT NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma               substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility          substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, context_type_id, attestation_type_id)
    -- FK to substrate.entity(hash) application-enforced.
) PARTITION BY HASH (entity_hash);

COMMENT ON TABLE substrate.entity_significance IS
    'Glicko-2 trust per (entity, arena, attestation_type). Hash-only entity reference. Hash-partitioned by entity_hash (modulus 8) for write-path concurrency. Stratified by attestation_type so kinds of evidence remain distinguishable; query-time blend collapses them when desired.';
