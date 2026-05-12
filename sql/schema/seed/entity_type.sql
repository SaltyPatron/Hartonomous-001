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
    ('tokenizer_model',    'model_weights');
