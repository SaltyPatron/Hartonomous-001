-- Bigram extraction from content trajectories → sequence_following arena
-- edges. Walks substrate.text_composition / paragraph / document content
-- entities, decodes their LINESTRINGZM child manifest via
-- substrate.get_composition_children, and emits often_follows(A, B) edges
-- weighted by global frequency.
--
-- Idempotent: ON CONFLICT DO NOTHING on edge insertion; edge_significance
-- updated in place via record_attestations_bulk-equivalent INSERT-SELECT
-- with sum aggregation.
--
-- Build-a-bear's next-token prior comes from this. Without it the
-- synthesizer's per-layer adjacency captures classification + semantic +
-- syntactic structure but not sequence-following — model knows "Hello"
-- clusters with greetings but doesn't know "Hello" is followed by
-- "world" / "how" / "," in real sentences.
CREATE OR REPLACE FUNCTION substrate.populate_sequence_following_edges(
    p_provenance_code TEXT DEFAULT 'tatoeba',
    p_min_frequency   INT DEFAULT 2
)
RETURNS TABLE(edges_emitted BIGINT, pairs_observed BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_provenance_id    INT;
    v_edge_type_id     INT;
    v_arena_id         INT;
    v_pos_evidence_id  INT;
    v_text_comp_type_id INT;
    v_paragraph_type_id INT;
    v_document_type_id  INT;
    v_source_role_id   INT;
    v_target_role_id   INT;
    v_edges_emitted    BIGINT := 0;
    v_pairs_observed   BIGINT := 0;
BEGIN
    SELECT id INTO v_provenance_id FROM substrate.provenance WHERE code = p_provenance_code;
    IF v_provenance_id IS NULL THEN
        RAISE EXCEPTION 'unknown provenance: %', p_provenance_code;
    END IF;

    SELECT id INTO v_edge_type_id FROM substrate.edge_type WHERE code = 'often_follows';
    IF v_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'edge_type "often_follows" not seeded; add to seed/edge_type.sql';
    END IF;

    SELECT id INTO v_arena_id FROM substrate.significance_context WHERE code = 'sequence_following';
    IF v_arena_id IS NULL THEN
        RAISE EXCEPTION 'significance_context "sequence_following" not seeded';
    END IF;

    SELECT id INTO v_pos_evidence_id FROM substrate.attestation_type WHERE code = 'positive_evidence';

    SELECT id INTO v_text_comp_type_id FROM substrate.entity_type WHERE code = 'text_composition';
    SELECT id INTO v_paragraph_type_id FROM substrate.entity_type WHERE code = 'paragraph';
    SELECT id INTO v_document_type_id  FROM substrate.entity_type WHERE code = 'document';

    SELECT id INTO v_source_role_id FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO v_target_role_id FROM substrate.edge_role WHERE code = 'target';

    -- Pairs: aggregate (A, B) bigram frequency across all content
    -- trajectories. The trajectory IS the ordered child manifest; consecutive
    -- children at ordinal n and n+1 form a bigram.
    DROP TABLE IF EXISTS pg_temp.bigram_freq;
    CREATE TEMP TABLE pg_temp.bigram_freq AS
    WITH content_entities AS (
        SELECT DISTINCT ec.entity_hash
          FROM substrate.entity_classification ec
         WHERE ec.entity_type_id IN (v_text_comp_type_id, v_paragraph_type_id, v_document_type_id)
    ),
    ordered_children AS (
        SELECT
            ce.entity_hash AS parent_hash,
            ch.ordinal,
            ch.child_hash,
            ROW_NUMBER() OVER (PARTITION BY ce.entity_hash ORDER BY ch.ordinal) AS rn
          FROM content_entities ce,
               LATERAL substrate.get_composition_children(ce.entity_hash) ch
    ),
    bigrams AS (
        SELECT
            a.child_hash AS source_hash,
            b.child_hash AS target_hash
          FROM ordered_children a
          JOIN ordered_children b
            ON b.parent_hash = a.parent_hash
           AND b.rn = a.rn + 1
         WHERE a.child_hash <> b.child_hash
    )
    SELECT
        source_hash,
        target_hash,
        count(*)::BIGINT AS freq
      FROM bigrams
     GROUP BY source_hash, target_hash
    HAVING count(*) >= p_min_frequency;

    SELECT count(*) INTO v_pairs_observed FROM pg_temp.bigram_freq;

    -- Compute edge hash per pair (BLAKE3 of edge_type_id + role-ordered
    -- participant hashes). We use the substrate helper if present;
    -- otherwise fall back to per-row hashing via the C extension.
    DROP TABLE IF EXISTS pg_temp.bigram_edge;
    CREATE TEMP TABLE pg_temp.bigram_edge AS
    SELECT
        bf.source_hash,
        bf.target_hash,
        hartonomous.blake3_edge_hash(v_edge_type_id::INT,
            ARRAY[bf.source_hash, bf.target_hash]::BYTEA[]) AS edge_hash,
        bf.freq
      FROM pg_temp.bigram_freq bf;

    -- Insert edges. ON CONFLICT skips already-existing identities.
    INSERT INTO substrate.edge (edge_type_id, hash, provenance_id, geom)
    SELECT v_edge_type_id, be.edge_hash, v_provenance_id, NULL
      FROM pg_temp.bigram_edge be
    ON CONFLICT (edge_type_id, hash) DO NOTHING;

    -- Insert edge_members (source + target roles).
    INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
    SELECT v_edge_type_id, be.edge_hash, be.source_hash, v_source_role_id, 0
      FROM pg_temp.bigram_edge be
    ON CONFLICT (edge_type_id, edge_hash, role_position) DO NOTHING;

    INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
    SELECT v_edge_type_id, be.edge_hash, be.target_hash, v_target_role_id, 1
      FROM pg_temp.bigram_edge be
    ON CONFLICT (edge_type_id, edge_hash, role_position) DO NOTHING;

    -- Edge significance: mu calibrated to log(1 + freq) so high-frequency
    -- bigrams dominate but no single super-frequent pair saturates.
    -- Baseline 1500 + 100 × log(1 + freq) puts freq=1 at mu=1500, freq=10
    -- at mu=1739, freq=1000 at mu=2191, freq=100000 at mu=2651.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id, mu, sigma, volatility, games)
    SELECT
        v_arena_id,
        v_edge_type_id,
        be.edge_hash,
        v_pos_evidence_id,
        1500.0 + 100.0 * ln(1 + be.freq),
        350.0,
        0.06,
        be.freq::INT
      FROM pg_temp.bigram_edge be
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO UPDATE
       SET mu = EXCLUDED.mu,
           games = substrate.edge_significance.games + EXCLUDED.games;

    GET DIAGNOSTICS v_edges_emitted = ROW_COUNT;

    edges_emitted := v_edges_emitted;
    pairs_observed := v_pairs_observed;
    RETURN NEXT;
END;
$$;

COMMENT ON FUNCTION substrate.populate_sequence_following_edges(TEXT, INT) IS
    'Walks substrate text_composition / paragraph / document content trajectories, extracts adjacent (A, B) bigrams, aggregates frequency, emits often_follows edges in the sequence_following arena weighted by ln(1+freq). Build-a-bear next-token prior source.';
