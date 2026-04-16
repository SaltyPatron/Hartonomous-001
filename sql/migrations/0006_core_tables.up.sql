-- 0006_core_tables.up.sql
-- Partitioned core data tables per specs/sql/partitioning.md.
-- Partition key IDs correspond to SERIAL IDs seeded in 0005.

-- ======================================================================
-- entity (partitioned by entity_type_id)
-- ======================================================================
CREATE TABLE substrate.entity (
    id             BIGSERIAL,
    hash           substrate.hash_value NOT NULL,
    entity_type_id INT NOT NULL REFERENCES substrate.entity_type(id),
    PRIMARY KEY (id, entity_type_id),
    UNIQUE (hash, entity_type_id)
) PARTITION BY LIST (entity_type_id);

CREATE TABLE substrate.entity_codepoint PARTITION OF substrate.entity FOR VALUES IN (1);
CREATE TABLE substrate.entity_grapheme   PARTITION OF substrate.entity FOR VALUES IN (2);
CREATE TABLE substrate.entity_word       PARTITION OF substrate.entity FOR VALUES IN (3);
CREATE TABLE substrate.entity_morpheme   PARTITION OF substrate.entity FOR VALUES IN (4);
CREATE TABLE substrate.entity_lemma      PARTITION OF substrate.entity FOR VALUES IN (5);
CREATE TABLE substrate.entity_ud_sentence PARTITION OF substrate.entity FOR VALUES IN (6);
CREATE TABLE substrate.entity_ud_token   PARTITION OF substrate.entity FOR VALUES IN (7);
CREATE TABLE substrate.entity_tatoeba    PARTITION OF substrate.entity FOR VALUES IN (8);
CREATE TABLE substrate.entity_text       PARTITION OF substrate.entity FOR VALUES IN (9, 10, 11, 12);
CREATE TABLE substrate.entity_semantic   PARTITION OF substrate.entity FOR VALUES IN (13, 14, 15, 16);
CREATE TABLE substrate.entity_unicode    PARTITION OF substrate.entity FOR VALUES IN (17, 18);
CREATE TABLE substrate.entity_image      PARTITION OF substrate.entity FOR VALUES IN (19);
CREATE TABLE substrate.entity_audio      PARTITION OF substrate.entity FOR VALUES IN (20, 21);
CREATE TABLE substrate.entity_video      PARTITION OF substrate.entity FOR VALUES IN (22);
CREATE TABLE substrate.entity_model      PARTITION OF substrate.entity FOR VALUES IN (23, 24, 25);
CREATE TABLE substrate.entity_default    PARTITION OF substrate.entity DEFAULT;

COMMENT ON TABLE substrate.entity IS 'Entities are content-addressed substrate nodes. Partitioned by entity_type_id.';

-- ======================================================================
-- edge (partitioned by edge_type_id)
-- ======================================================================
CREATE TABLE substrate.edge (
    id            BIGSERIAL,
    hash          substrate.hash_value NOT NULL,
    edge_type_id  INT NOT NULL REFERENCES substrate.edge_type(id),
    geom          geometry(GeometryZM),
    provenance_id INT NOT NULL REFERENCES substrate.provenance(id),
    PRIMARY KEY (id, edge_type_id),
    UNIQUE (hash, edge_type_id)
) PARTITION BY LIST (edge_type_id);

-- 0005 seeded 32 edge types: 1..13 structural, 14..16 cross_lingual, 17..18 cross_modal, 19..21 unicode, 22..32 model_derived.
CREATE TABLE substrate.edge_structural PARTITION OF substrate.edge
    FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
CREATE TABLE substrate.edge_cross_lingual PARTITION OF substrate.edge
    FOR VALUES IN (14, 15, 16);
CREATE TABLE substrate.edge_cross_modal PARTITION OF substrate.edge
    FOR VALUES IN (17, 18);
CREATE TABLE substrate.edge_unicode PARTITION OF substrate.edge
    FOR VALUES IN (19, 20, 21);
CREATE TABLE substrate.edge_model PARTITION OF substrate.edge
    FOR VALUES IN (22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33);
CREATE TABLE substrate.edge_default PARTITION OF substrate.edge DEFAULT;

COMMENT ON TABLE substrate.edge IS 'Typed directed/undirected substrate edges with geometric trajectories. Partitioned by edge_type_id.';

-- ======================================================================
-- physicality (partitioned by physicality_type_id)
-- ======================================================================
CREATE TABLE substrate.physicality (
    id                  BIGSERIAL,
    entity_id           BIGINT NOT NULL,
    physicality_type_id INT NOT NULL REFERENCES substrate.physicality_type(id),
    geom                geometry(GeometryZM) NOT NULL,
    PRIMARY KEY (id, physicality_type_id)
) PARTITION BY LIST (physicality_type_id);

-- 0005 seeded physicality_type: 1=s3_position, 2=hilbert_value, 3..10 audio, 11..12 model, 13 contour.
CREATE TABLE substrate.physicality_s3      PARTITION OF substrate.physicality FOR VALUES IN (1);
CREATE TABLE substrate.physicality_hilbert PARTITION OF substrate.physicality FOR VALUES IN (2);
CREATE TABLE substrate.physicality_audio   PARTITION OF substrate.physicality FOR VALUES IN (3, 4, 5, 6, 7, 8, 9, 10);
CREATE TABLE substrate.physicality_model   PARTITION OF substrate.physicality FOR VALUES IN (11, 12);
CREATE TABLE substrate.physicality_image   PARTITION OF substrate.physicality FOR VALUES IN (13);
CREATE TABLE substrate.physicality_default PARTITION OF substrate.physicality DEFAULT;

COMMENT ON TABLE substrate.physicality IS 'Geometric realizations of entities. Partitioned by physicality_type_id.';

-- ======================================================================
-- significance (partitioned by context_type_id)
-- ======================================================================
CREATE TABLE substrate.significance (
    id              BIGSERIAL,
    entity_id       BIGINT,
    edge_id         BIGINT,
    context_type_id INT NOT NULL REFERENCES substrate.significance_context(id),
    mu              substrate.significance_mu NOT NULL DEFAULT 1500.0,
    sigma           substrate.significance_sigma NOT NULL DEFAULT 350.0,
    volatility      substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (id, context_type_id),
    CHECK ((entity_id IS NOT NULL) <> (edge_id IS NOT NULL))
) PARTITION BY LIST (context_type_id);

-- 0005 seeded significance_context 1..10 in documented order.
CREATE TABLE substrate.significance_lexical        PARTITION OF substrate.significance FOR VALUES IN (1);
CREATE TABLE substrate.significance_syntactic      PARTITION OF substrate.significance FOR VALUES IN (2);
CREATE TABLE substrate.significance_translation    PARTITION OF substrate.significance FOR VALUES IN (3);
CREATE TABLE substrate.significance_model          PARTITION OF substrate.significance FOR VALUES IN (4);
CREATE TABLE substrate.significance_authority      PARTITION OF substrate.significance FOR VALUES IN (5);
CREATE TABLE substrate.significance_relevance      PARTITION OF substrate.significance FOR VALUES IN (6);
CREATE TABLE substrate.significance_corroboration  PARTITION OF substrate.significance FOR VALUES IN (7);
CREATE TABLE substrate.significance_frequency      PARTITION OF substrate.significance FOR VALUES IN (8);
CREATE TABLE substrate.significance_attention      PARTITION OF substrate.significance FOR VALUES IN (9);
CREATE TABLE substrate.significance_morphological  PARTITION OF substrate.significance FOR VALUES IN (10);

COMMENT ON TABLE substrate.significance IS 'Glicko-2 ratings in arena contexts. Partitioned by context_type_id. No DEFAULT — new arenas require explicit partition.';

-- ======================================================================
-- sequence (unpartitioned — composition relationships)
-- ======================================================================
CREATE TABLE substrate.sequence (
    id               BIGSERIAL PRIMARY KEY,
    parent_id        BIGINT NOT NULL,
    child_id         BIGINT NOT NULL,
    ordinal_position substrate.ordinal_position NOT NULL,
    rle_count        substrate.rle_count NOT NULL DEFAULT 1
);
CREATE INDEX idx_sequence_parent ON substrate.sequence(parent_id, ordinal_position);
CREATE INDEX idx_sequence_child ON substrate.sequence(child_id);

COMMENT ON TABLE substrate.sequence IS 'Composition relationships (parent entity → ordered child entities). Run-length encoded.';

-- ======================================================================
-- edge_member (unpartitioned — n-ary edge participants)
-- ======================================================================
CREATE TABLE substrate.edge_member (
    edge_id      BIGINT NOT NULL,
    entity_id    BIGINT NOT NULL,
    edge_role_id INT NOT NULL REFERENCES substrate.edge_role(id),
    PRIMARY KEY (edge_id, entity_id, edge_role_id)
);
CREATE INDEX idx_edge_member_entity ON substrate.edge_member(entity_id, edge_id);
CREATE INDEX idx_edge_member_role ON substrate.edge_member(edge_role_id, edge_id);

COMMENT ON TABLE substrate.edge_member IS 'N-ary edge participants with roles. Co-located with edge — not partitioned.';
