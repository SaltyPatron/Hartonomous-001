-- Entity types. Content-only — every row classifies CONTENT.
--
-- Identity is BLAKE3 over content bytes (per docs/00-substrate-spec.md §II.1).
-- Same content under multiple structural classifications collapses to one
-- entity row with multiple substrate.entity_classification rows.
--
-- Per docs/01-tensor-primitive-spec.md: per-role units of model tensors are
-- attestation EDGES between content entities (NOT separate entity types).
-- Per-tensor analytical surfaces (sparsity, weight distribution, SVD spectrum,
-- etc.) are physicality on the tensor entity (NOT separate entity types).
INSERT INTO substrate.entity_type (code, modality) VALUES
    -- Text
    ('codepoint',          'text'),
    ('grapheme_cluster',   'text'),
    ('word_form',          'text'),
    ('morpheme',           'text'),
    ('lemma',              'text'),
    ('text_composition',   'text'),
    ('paragraph',          'text'),
    ('document',           'text'),
    ('synset',             'text'),
    ('collation_element',  'text'),
    ('language_name',      'text'),
    -- Image
    ('pixel_region',       'image'),
    ('visual_concept',     'image'),
    ('object_query',       'image'),
    -- Audio
    ('audio_recording',    'audio'),
    ('audio_chunk',        'audio'),
    ('codec_codevector',   'audio'),
    -- Video
    ('video_frame',        'video'),
    -- Model package artifacts
    ('tensor',             'model_weights'),
    ('model_architecture', 'model_weights'),
    ('model_package',      'model_weights'),
    ('model_package_tensor','model_weights'),
    ('tokenizer_model',    'model_weights'),
    -- Reference-vocabulary entities (AP-8 correction, 2026-05-14):
    -- POS / lexname / language / morph feature / deprel / sense codes
    -- become content-hashed substrate entities. Corpus decomposers emit
    -- typed edges (has_pos, has_lexname, has_language, has_morph_feature,
    -- has_deprel_pattern) into these as edge targets. The unified Glicko-2
    -- surface (substrate.edge_significance) per (provenance × arena) is
    -- the authoritative consensus surface; legacy junction tables
    -- (entity_pos, entity_lexname, entity_language, entity_morph_feature,
    -- pattern_deprel) remain as denormalized analytics caches per AP-8.
    -- Identity = BLAKE3("{kind}:{code}") via
    -- Hartonomous.Core.Compute.Common.ReferenceVocabularyHashes.
    ('pos',                'text'),
    ('lexname',            'text'),
    ('morph_feature',      'text'),
    ('deprel',             'text'),
    ('sense',              'text'),
    -- Per-codepoint UCD property classifications (Gate 1 #38 refactor,
    -- 2026-05-18): each Unicode property code (general_category Lu, script
    -- Latn, block "Basic Latin", bidi_class AL, east_asian_width W,
    -- break_property "GCB:CR") becomes a content-hashed substrate entity
    -- via ReferenceVocabularyHashes.{GeneralCategory,Script,Block,BidiClass,
    -- EastAsianWidth,BreakProperty}EntityHash. Codepoint atoms emit typed
    -- edges (has_cp_general_category etc.) into these as edge targets.
    -- Cross-UCD-version attestation accumulates on the same edge identities
    -- under the unicode_version_consensus arena.
    ('general_category',   'text'),
    ('script',             'text'),
    ('block',              'text'),
    ('bidi_class',         'text'),
    ('east_asian_width',   'text'),
    ('break_property',     'text');
