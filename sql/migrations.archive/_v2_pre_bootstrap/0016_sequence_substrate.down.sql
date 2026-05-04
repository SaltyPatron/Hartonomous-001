DROP FUNCTION IF EXISTS substrate.flush_sequence_from_staging();
DROP FUNCTION IF EXISTS substrate.recompose_text(INT, BYTEA, INT);
DROP FUNCTION IF EXISTS substrate.composition_parents(INT, BYTEA);
DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(INT, BYTEA, INT, INT);
DROP FUNCTION IF EXISTS substrate.composition_range(INT, BYTEA, INT, INT);
DROP FUNCTION IF EXISTS substrate.composition_after(INT, BYTEA, INT, INT);
DROP FUNCTION IF EXISTS substrate.composition_before(INT, BYTEA, INT, INT);
DROP FUNCTION IF EXISTS substrate.composition_at(INT, BYTEA, INT);

DROP TABLE IF EXISTS substrate.sequence_default;
DROP TABLE IF EXISTS substrate.sequence_model;
DROP TABLE IF EXISTS substrate.sequence_video;
DROP TABLE IF EXISTS substrate.sequence_audio;
DROP TABLE IF EXISTS substrate.sequence_image;
DROP TABLE IF EXISTS substrate.sequence_unicode;
DROP TABLE IF EXISTS substrate.sequence_semantic;
DROP TABLE IF EXISTS substrate.sequence_text;
DROP TABLE IF EXISTS substrate.sequence_tatoeba;
DROP TABLE IF EXISTS substrate.sequence_ud_token;
DROP TABLE IF EXISTS substrate.sequence_ud_sentence;
DROP TABLE IF EXISTS substrate.sequence_lemma;
DROP TABLE IF EXISTS substrate.sequence_morpheme;
DROP TABLE IF EXISTS substrate.sequence_word;
DROP TABLE IF EXISTS substrate.sequence_grapheme;
DROP TABLE IF EXISTS substrate.sequence_codepoint;
DROP TABLE IF EXISTS substrate.sequence;

-- Re-create the old has_constituent edge type so 0015 invariants hold if a
-- redeploy walks downward then upward through 0015.
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_constituent', 'structural', NULL, NULL)
ON CONFLICT (code) DO NOTHING;
