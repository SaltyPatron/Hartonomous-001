-- 0005_phase1_seed.up.sql
-- Phase 1 bootstrap seed data per specs/sql/seed-scripts.md.
-- Insertion ORDER is load-bearing: SERIAL IDs must match partition definitions in 0006.

-- 1. Entity type (25 rows → IDs 1..25)
INSERT INTO substrate.entity_type (code, modality) VALUES
    ('codepoint',           'text'),
    ('grapheme_cluster',    'text'),
    ('word_form',           'text'),
    ('morpheme',            'text'),
    ('lemma',               'text'),
    ('ud_sentence',         'text'),
    ('ud_token',            'text'),
    ('tatoeba_sentence',    'text'),
    ('text_composition',    'text'),
    ('paragraph',           'text'),
    ('document',            'text'),
    ('bpe_token',           'text'),
    ('synset',              'text'),
    ('word_sense',          'text'),
    ('wikt_sense',          'text'),
    ('inflected_form',      'text'),
    ('collation_element',   'text'),
    ('language_name',       'text'),
    ('pixel_region',        'image'),
    ('audio_recording',     'audio'),
    ('audio_chunk',         'audio'),
    ('video_frame',         'video'),
    ('tensor',              'model_weights'),
    ('model_architecture',  'model_weights'),
    ('attention_pattern',   'model_weights');

-- 2. Physicality type (13 rows → IDs 1..13)
INSERT INTO substrate.physicality_type (code) VALUES
    ('s3_position'),
    ('hilbert_value'),
    ('waveform'),
    ('fft_spectrum'),
    ('stft_spectrogram'),
    ('pitch_contour'),
    ('formant_trajectory'),
    ('spectral_centroid'),
    ('mfcc_frame'),
    ('chromagram'),
    ('svd_spectrum'),
    ('weight_distribution'),
    ('contour');

-- 3. Edge role (7 rows)
INSERT INTO substrate.edge_role (code) VALUES
    ('source'), ('target'), ('context'), ('mediator'),
    ('evidence'), ('head'), ('dependent');

-- 4. Significance context (10 rows → IDs 1..10)
INSERT INTO substrate.significance_context (code) VALUES
    ('lexical_disambiguation'),
    ('syntactic_role_fitness'),
    ('translation_quality'),
    ('model_trust'),
    ('source_authority'),
    ('semantic_relevance'),
    ('corroboration_strength'),
    ('frequency_significance'),
    ('attention_pattern_confidence'),
    ('morphological_productivity');

-- 5. Provenance (10 rows)
INSERT INTO substrate.provenance (code, curator_class, initial_mu) VALUES
    ('unicode_consortium',   'authoritative_standard',  2000.0),
    ('sil_international',    'authoritative_standard',  2000.0),
    ('princeton_wordnet',    'academic_curated',        1800.0),
    ('omwn_consortium',      'academic_consortium',     1600.0),
    ('universaldependencies','academic_consortium',     1600.0),
    ('wiktextract',          'community_curated',       1400.0),
    ('tatoeba',              'community_contributed',   1200.0),
    ('huggingface_model',    'model_derived',           1500.0),
    ('user_session',         'user_input',              1000.0),
    ('system_computed',      'system_computed',         1300.0);

-- 6. Lexname (45 rows)
INSERT INTO substrate.lexname (code) VALUES
    ('adj.all'), ('adj.pert'), ('adj.ppl'),
    ('adv.all'),
    ('noun.Tops'), ('noun.act'), ('noun.animal'), ('noun.artifact'), ('noun.attribute'),
    ('noun.body'), ('noun.cognition'), ('noun.communication'), ('noun.event'),
    ('noun.feeling'), ('noun.food'), ('noun.group'), ('noun.location'), ('noun.motive'),
    ('noun.object'), ('noun.person'), ('noun.phenomenon'), ('noun.plant'),
    ('noun.possession'), ('noun.process'), ('noun.quantity'), ('noun.relation'),
    ('noun.shape'), ('noun.state'), ('noun.substance'), ('noun.time'),
    ('verb.body'), ('verb.change'), ('verb.cognition'), ('verb.communication'),
    ('verb.competition'), ('verb.consumption'), ('verb.contact'), ('verb.creation'),
    ('verb.emotion'), ('verb.motion'), ('verb.perception'), ('verb.possession'),
    ('verb.social'), ('verb.stative'), ('verb.weather');

-- 7. POS top-level (17 rows)
INSERT INTO substrate.pos (code, parent_id) VALUES
    ('ADJ', NULL), ('ADP', NULL), ('ADV', NULL), ('AUX', NULL),
    ('CCONJ', NULL), ('DET', NULL), ('INTJ', NULL), ('NOUN', NULL),
    ('NUM', NULL), ('PART', NULL), ('PRON', NULL), ('PROPN', NULL),
    ('PUNCT', NULL), ('SCONJ', NULL), ('SYM', NULL), ('VERB', NULL),
    ('X', NULL);

-- 8. Edge type bootstrap (32 rows → IDs 1..32)
-- Order matters: partition 0006 groups IDs 1..13 structural, 14..16 cross_lingual, 17..18 cross_modal, 19..21 unicode, 22..32 model_derived.
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_sense',             'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma'),
        (SELECT id FROM substrate.entity_type WHERE code = 'synset')),
    ('has_form',              'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma'),
        (SELECT id FROM substrate.entity_type WHERE code = 'inflected_form')),
    ('has_lemma',             'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'word_form'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma')),
    ('has_morpheme',          'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'word_form'),
        (SELECT id FROM substrate.entity_type WHERE code = 'morpheme')),
    ('has_gloss',             'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'synset'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_example',           'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'synset'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_name',              'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_text',              'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'tatoeba_sentence'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('inflection_of',         'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'inflected_form'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma')),
    ('has_etymology',         'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_pronunciation',     'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_hyphenation',       'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_wikidata',          'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('aligned_to_synset',     'cross_lingual',
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma'),
        (SELECT id FROM substrate.entity_type WHERE code = 'synset')),
    ('translation_of',        'cross_lingual',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma')),
    ('translation_link',      'cross_lingual',
        (SELECT id FROM substrate.entity_type WHERE code = 'tatoeba_sentence'),
        (SELECT id FROM substrate.entity_type WHERE code = 'tatoeba_sentence')),
    ('recording_of',          'cross_modal',
        (SELECT id FROM substrate.entity_type WHERE code = 'audio_recording'),
        (SELECT id FROM substrate.entity_type WHERE code = 'tatoeba_sentence')),
    ('has_contributor',       'cross_modal',
        (SELECT id FROM substrate.entity_type WHERE code = 'audio_recording'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('maps_to_lowercase',     'unicode',
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint'),
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint')),
    ('case_folds_to',         'unicode',
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint'),
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint')),
    ('has_collation_weight',  'unicode',
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint'),
        (SELECT id FROM substrate.entity_type WHERE code = 'collation_element')),
    ('in_model',              'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture')),
    ('in_layer',              'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture')),
    ('has_dtype',             'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_shape',             'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_hidden_size',       'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_num_layers',        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_num_attention_heads','model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_vocab_size',        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_token_string',      'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'bpe_token'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_token_id',          'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'bpe_token'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('in_vocabulary',         'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'bpe_token'),
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture')),
    ('co_occurrence',         'model_derived', NULL, NULL);

-- Validation: halt if counts deviate from the bootstrap spec.
DO $$
DECLARE
    cnt INT;
BEGIN
    SELECT COUNT(*) INTO cnt FROM substrate.entity_type;
    IF cnt <> 25 THEN RAISE EXCEPTION 'entity_type count=% (expected 25)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.physicality_type;
    IF cnt <> 13 THEN RAISE EXCEPTION 'physicality_type count=% (expected 13)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.edge_role;
    IF cnt <> 7 THEN RAISE EXCEPTION 'edge_role count=% (expected 7)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.significance_context;
    IF cnt <> 10 THEN RAISE EXCEPTION 'significance_context count=% (expected 10)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.provenance;
    IF cnt <> 10 THEN RAISE EXCEPTION 'provenance count=% (expected 10)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.lexname;
    IF cnt <> 45 THEN RAISE EXCEPTION 'lexname count=% (expected 45)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.pos;
    IF cnt <> 17 THEN RAISE EXCEPTION 'pos count=% (expected 17)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.edge_type;
    -- 13 structural + 3 cross_lingual + 2 cross_modal + 3 unicode + 12 model_derived = 33.
    -- (seed-scripts.md prose says 32, its own itemized list sums to 33 — the list is authoritative.)
    IF cnt <> 33 THEN RAISE EXCEPTION 'edge_type count=% (expected 33)', cnt; END IF;
END$$;
