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
    -- Text — proper UAX-29 + morphological + semantic tier ladder.
    -- Trunk-to-leaf walk (per the substrate's recursive Merkle composition):
    --   document      → trajectory through paragraph entities
    --   paragraph     → trajectory through sentence entities (UAX-29 paragraph break)
    --   sentence      → trajectory through phrase/word_form entities (UAX-29 SB)
    --   phrase        → trajectory through word_form entities (syntactic, derived from
    --                   dependency parsing where available; optional intermediate tier)
    --   word_form     → trajectory through grapheme_cluster entities (UAX-29 WB)
    --   morpheme      → trajectory through grapheme_cluster entities (morphological
    --                   decomposition; subword piece of a word_form)
    --   grapheme_cluster → trajectory through codepoint entities (UAX-29 GB)
    --   codepoint     → POINTZM atom (Super-Fibonacci S³ by UCA rank in physicality)
    --
    -- Off-trunk classifications (typed edges from the trunk tiers, NOT
    -- composition children):
    --   lemma         — canonical form, target of has_lemma edge from word_form
    --   synset        — semantic cluster, target of has_sense edge from lemma
    --   collation_element — UCA collation, target of has_collation edge from grapheme/cp
    --   language_name — ISO 639 language identity, target of has_language edge
    --
    -- text_composition is a generic fallback for "non-tier-specific text content"
    -- (e.g. a named sequence emoji bundle, a multi-codepoint Unicode standardized
    -- variant) that doesn't fit a specific UAX-29 tier. Prefer the tier-specific
    -- types when emitting; text_composition should shrink over time as the
    -- decomposer learns to classify content into proper tiers.
    ('codepoint',          'text'),
    ('grapheme_cluster',   'text'),
    ('word_form',          'text'),
    ('morpheme',           'text'),
    ('lemma',              'text'),
    ('phrase',             'text'),
    ('sentence',           'text'),
    ('paragraph',          'text'),
    ('document',           'text'),
    ('text_composition',   'text'),
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
    ('break_property',     'text'),
    -- Recipe entities: content-addressed (BLAKE3 of canonical recipe
    -- JSON). App-tier starter recipes seeded at bootstrap; substrate-tier
    -- auto-derived from SafetensorsDecomposer ingest; user-tier from
    -- practitioner forks. Stored in substrate.recipe + named via
    -- substrate.recipe_name.
    ('recipe',             'text');
