-- Edge types. Single INSERT...SELECT pattern: tuples in a VALUES CTE,
-- resolved against substrate.entity_type via JOIN. NULL source/target codes
-- mean polymorphic.
--
-- semantic_weight is a structural prior on the relation strength used by
-- engine traversal as a tie-breaker; arena-bound Glicko mu on
-- substrate.edge_significance is the dynamic weight.
--
-- Categories:
--   structural    — within-modality structural composition (text)
--   cross_lingual — between language entities
--   cross_modal   — between content-entity-types of different modalities
--   unicode       — codepoint-level Unicode tables
--   model_derived — model-package metadata + content-entity attestations
--                   produced by safetensors decomposers (per docs/01-tensor-
--                   primitive-spec.md §IV)
--   semantic      — WordNet / Wiktionary semantic relations between synsets
--                   and lemmas
--
-- Per docs/01-tensor-primitive-spec.md: there is no has_<phantom> edge type
-- pointing to a phantom entity. Per-tuple attestations land on edges between
-- content entities; per-tensor analytics live as physicality on the tensor
-- entity. The model_derived edges below are EXACTLY:
--   * Architecture metadata (in_model, in_layer, has_dtype, has_shape,
--     has_hidden_size, has_num_layers, has_num_attention_heads, has_vocab_size,
--     has_token_id, in_vocabulary, has_tensor, has_architecture_name,
--     has_tensor_name, has_tokenizer_model, has_token_in_tokenizer)
--   * Token↔token attestation surfaces (model_concept_similarity,
--     model_attention_pattern, model_ffn_factor)
--   * Cross-content attestation surfaces (model_cross_modal_pattern,
--     model_spatial_pattern, model_detection_class)
--   * Vocab-coverage join (covers_lemma)
--   * co_occurrence (polymorphic — used by corpus-window decomposers)

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id, semantic_weight)
SELECT
    s.code,
    s.category,
    src.id,
    tgt.id,
    CASE
        WHEN s.code IN (
            'member_holonym', 'substance_holonym', 'part_holonym',
            'member_meronym', 'substance_meronym', 'part_meronym', 'has_morpheme'
        ) THEN 0.9
        WHEN s.code IN (
            'translation_of', 'aligned_to_synset', 'translation_link'
        ) THEN 0.85
        WHEN s.code IN (
            'has_etymology', 'has_pronunciation', 'has_hyphenation', 'has_wikidata'
        ) THEN 0.7
        WHEN s.code IN (
            'similar_to', 'also_see', 'verb_group', 'attribute', 'derivationally_related'
        ) THEN 0.6
        WHEN s.code IN (
            'synonym', 'related', 'coordinate_term', 'derived'
        ) THEN 0.5
        ELSE 1.0
    END AS semantic_weight
FROM (VALUES
    -- ── Structural (within text modality) ──────────────────────────────
    ('has_sense',                'structural',    'lemma',              'synset'),              --  1
    ('has_form',                 'structural',    'lemma',              'word_form'),           --  2
    ('has_lemma',                'structural',    'word_form',          'lemma'),               --  3
    ('has_morpheme',             'structural',    'word_form',          'morpheme'),            --  4
    ('has_gloss',                'structural',    'synset',             'text_composition'),    --  5
    ('has_example',              'structural',    'synset',             'text_composition'),    --  6
    ('has_name',                 'structural',    'model_architecture', 'text_composition'),    --  7
    ('inflection_of',            'structural',    'word_form',          'lemma'),               --  8
    ('has_etymology',            'structural',    'lemma',              'text_composition'),    --  9
    ('has_pronunciation',        'structural',    'lemma',              'text_composition'),    -- 10
    ('has_hyphenation',          'structural',    'lemma',              'text_composition'),    -- 11
    ('has_wikidata',             'structural',    'lemma',              'text_composition'),    -- 12
    ('lexicalized_compound',     'structural',    'word_form',          'word_form'),           -- 13
    ('has_frame',                'structural',    'lemma',              'text_composition'),    -- 14
    -- ── Cross-lingual ──────────────────────────────────────────────────
    ('aligned_to_synset',        'cross_lingual', 'lemma',              'synset'),              -- 16
    ('translation_of',           'cross_lingual', 'lemma',              'lemma'),               -- 17
    ('translation_link',         'cross_lingual', 'text_composition',   'text_composition'),    -- 18
    ('macrolanguage_contains',   'cross_lingual', 'language_name',      'language_name'),       -- 19
    ('has_alternate_name',       'cross_lingual', 'language_name',      'language_name'),       -- 20
    ('superseded_by',            'cross_lingual', 'language_name',      'language_name'),       -- 21
    ('etym_inherited_from',      'cross_lingual', 'lemma',              'lemma'),               -- 22
    ('etym_derived_from',        'cross_lingual', 'lemma',              'lemma'),               -- 23
    ('etym_borrowed_from',       'cross_lingual', 'lemma',              'lemma'),               -- 24
    ('etym_cognate_with',        'cross_lingual', 'lemma',              'lemma'),               -- 25
    ('etym_calque_of',           'cross_lingual', 'lemma',              'lemma'),               -- 26
    ('etym_mention',             'cross_lingual', 'lemma',              'lemma'),               -- 27
    ('etym_link',                'cross_lingual', 'lemma',              'text_composition'),    -- 28
    ('etym_etymon',              'cross_lingual', 'lemma',              'lemma'),               -- 29
    -- ── Cross-modal ────────────────────────────────────────────────────
    ('recording_of',             'cross_modal',   'audio_recording',    'text_composition'),    -- 30
    ('has_contributor',          'cross_modal',   'audio_recording',    'text_composition'),    -- 31
    -- ── Unicode ────────────────────────────────────────────────────────
    ('maps_to_lowercase',        'unicode',       'codepoint',          'codepoint'),           -- 32
    ('case_folds_to',            'unicode',       'codepoint',          'codepoint'),           -- 33
    ('has_collation_weight',     'unicode',       'codepoint',          'collation_element'),   -- 34
    -- ── Model-derived: architecture + tokenizer + tensor metadata ──────
    ('in_model',                 'model_derived', 'tensor',             'model_architecture'),  -- 35
    ('in_layer',                 'model_derived', 'tensor',             'model_architecture'),  -- 36
    ('has_dtype',                'model_derived', 'tensor',             'text_composition'),    -- 37
    ('has_shape',                'model_derived', 'tensor',             'text_composition'),    -- 38
    ('has_hidden_size',          'model_derived', 'model_architecture', 'text_composition'),    -- 39
    ('has_num_layers',           'model_derived', 'model_architecture', 'text_composition'),    -- 40
    ('has_num_attention_heads',  'model_derived', 'model_architecture', 'text_composition'),    -- 41
    ('has_vocab_size',           'model_derived', 'model_architecture', 'text_composition'),    -- 42
    ('has_token_id',             'model_derived', 'word_form',          'text_composition'),    -- 43
    ('in_vocabulary',            'model_derived', 'word_form',          'model_architecture'),  -- 44
    ('has_tensor',               'model_derived', 'model_architecture', 'tensor'),              -- 45
    ('has_architecture_name',    'model_derived', 'model_architecture', 'text_composition'),    -- 46
    ('has_tensor_name',          'model_derived', 'tensor',             'text_composition'),    -- 47
    ('has_package_tensor_primitive',    'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_tuple',        'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_slot',         'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_layer_index',  'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_head_index',   'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_expert_index', 'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_modality',     'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_fused_slice',  'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_linearized_shape', 'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_tokenizer_model',      'model_derived', 'model_architecture', 'text_composition'),    -- 48
    ('has_token_in_tokenizer',   'model_derived', 'model_architecture', 'word_form'),           -- 49
    ('covers_lemma',             'model_derived', 'word_form',          'lemma'),               -- 50
    ('co_occurrence',            'model_derived', NULL,                 NULL),                  -- 51
    -- Model-package text artifact bindings: model_architecture → text_composition
    -- for the artifact's content. Same artifact across model snapshots collapses
    -- to ONE document with N has_*_artifact edges via content-addressed identity.
    ('has_config_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 52
    ('has_tokenizer_artifact',          'model_derived', 'model_architecture', 'text_composition'),  -- 53
    ('has_tokenizer_config_artifact',   'model_derived', 'model_architecture', 'text_composition'),  -- 54
    ('has_special_tokens_artifact',     'model_derived', 'model_architecture', 'text_composition'),  -- 55
    ('has_merges_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 56
    ('has_chat_template_artifact',      'model_derived', 'model_architecture', 'text_composition'),  -- 57
    ('has_generation_config_artifact',  'model_derived', 'model_architecture', 'text_composition'),  -- 58
    ('has_readme_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 59
    -- ── Model-derived: content-entity attestation surfaces ─────────────
    -- These are the load-bearing token↔token / patch↔patch / frame↔frame
    -- edges that accumulate per-tuple attestation events from every
    -- ingested model. Per docs/01-tensor-primitive-spec.md §IV.
    ('model_concept_similarity', 'model_derived', 'word_form',          'word_form'),           -- 52
    ('model_attention_pattern',  'model_derived', 'word_form',          'word_form'),           -- 53
    ('model_ffn_factor',         'model_derived', 'word_form',          'word_form'),           -- 54
    ('model_spatial_pattern',    'model_derived', NULL,                 NULL),                  -- 55
    ('model_cross_modal_pattern','model_derived', NULL,                 NULL),                  -- 56
    ('model_detection_class',    'model_derived', 'object_query',       'visual_concept'),      -- 57
    -- ── Semantic: WordNet pointers (synset ↔ synset) ────────────────────
    ('hypernym',                 'semantic',      'synset', 'synset'),                          -- 58
    ('hyponym',                  'semantic',      'synset', 'synset'),                          -- 59
    ('instance_hypernym',        'semantic',      'synset', 'synset'),                          -- 60
    ('instance_hyponym',         'semantic',      'synset', 'synset'),                          -- 61
    ('member_holonym',           'semantic',      'synset', 'synset'),                          -- 62
    ('substance_holonym',        'semantic',      'synset', 'synset'),                          -- 63
    ('part_holonym',             'semantic',      'synset', 'synset'),                          -- 64
    ('member_meronym',           'semantic',      'synset', 'synset'),                          -- 65
    ('substance_meronym',        'semantic',      'synset', 'synset'),                          -- 66
    ('part_meronym',             'semantic',      'synset', 'synset'),                          -- 67
    ('attribute',                'semantic',      'synset', 'synset'),                          -- 68
    ('derivationally_related',   'semantic',      'synset', 'synset'),                          -- 69
    ('antonym',                  'semantic',      'synset', 'synset'),                          -- 70
    ('similar_to',               'semantic',      'synset', 'synset'),                          -- 71
    ('also_see',                 'semantic',      'synset', 'synset'),                          -- 72
    ('verb_group',               'semantic',      'synset', 'synset'),                          -- 73
    ('entailment',               'semantic',      'synset', 'synset'),                          -- 74
    ('cause',                    'semantic',      'synset', 'synset'),                          -- 75
    ('participle_of_verb',       'semantic',      'synset', 'synset'),                          -- 76
    ('pertainym',                'semantic',      'synset', 'synset'),                          -- 77
    ('domain_of_synset_topic',   'semantic',      'synset', 'synset'),                          -- 78
    ('member_of_domain_topic',   'semantic',      'synset', 'synset'),                          -- 79
    ('domain_of_synset_region',  'semantic',      'synset', 'synset'),                          -- 80
    ('member_of_domain_region',  'semantic',      'synset', 'synset'),                          -- 81
    ('domain_of_synset_usage',   'semantic',      'synset', 'synset'),                          -- 82
    ('member_of_domain_usage',   'semantic',      'synset', 'synset'),                          -- 83
    -- ── Semantic: Wiktionary lemma ↔ lemma ─────────────────────────────
    ('synonym',                  'semantic',      'lemma',  'lemma'),                           -- 84
    ('coordinate_term',          'semantic',      'lemma',  'lemma'),                           -- 85
    ('derived',                  'semantic',      'lemma',  'lemma'),                           -- 86
    ('related',                  'semantic',      'lemma',  'lemma'),                           -- 87
    -- ── Unicode structural extensions (appended to preserve existing IDs) ─
    ('maps_to_uppercase',        'unicode',       'codepoint',          'codepoint'),           -- 96
    ('maps_to_titlecase',        'unicode',       'codepoint',          'codepoint'),           -- 97
    ('has_canonical_decomposition',      'unicode', 'codepoint',        'text_composition'),    -- 98
    ('has_compatibility_decomposition',  'unicode', 'codepoint',        'text_composition'),    -- 99
    ('canonical_composes_to',    'unicode',       'text_composition',   'codepoint'),           -- 100
    ('has_full_case_mapping',    'unicode',       'codepoint',          'text_composition'),    -- 101
    ('has_named_sequence',       'unicode',       'text_composition',   'text_composition'),    -- 102
    ('has_standardized_variant', 'unicode',       'codepoint',          'text_composition'),    -- 103
    ('has_emoji_sequence',       'unicode',       'text_composition',   'text_composition'),    -- 104
    ('has_emoji_zwj_sequence',   'unicode',       'text_composition',   'text_composition'),    -- 105
    ('confusable_with',          'unicode',       'text_composition',   'text_composition'),    -- 106
    ('idna_maps_to',             'unicode',       'codepoint',          'text_composition'),    -- 107
    ('has_bidi_mirroring_glyph', 'unicode',       'codepoint',          'codepoint'),           -- 108
    ('unihan_variant',           'unicode',       'codepoint',          'codepoint'),           -- 109
    ('unihan_reading',           'unicode',       'codepoint',          'text_composition'),    -- 110
    ('unihan_source',            'unicode',       'codepoint',          'text_composition'),    -- 111
    ('has_radical_stroke',       'unicode',       'codepoint',          'text_composition'),    -- 112
    -- Sequence-following bigram (Substrate Synthesis next-token prior). Populated
    -- by substrate.populate_sequence_following_edges from content trajectory
    -- ordinals. Source role = preceding token; target role = following token.
    -- Weighted by ln(1+freq) in sequence_following arena.
    ('often_follows',            'sequence',      'word_form',          'word_form'),           -- 113
    -- ── Cross-link (Unicode ↔ ISO / encoding-standard / CLDR) ──────────
    -- Per universal-cross-source-attestation: every text-bearing source
    -- attests cross-cuttingly. These edges land the cross-link semantic
    -- facts that previously had no substrate edge_type.
    ('has_iso_639_1_code',       'cross_lingual', 'language_name',      'text_composition'),    -- 114
    ('has_iso_639_2b_code',      'cross_lingual', 'language_name',      'text_composition'),    -- 115
    ('has_iso_639_2t_code',      'cross_lingual', 'language_name',      'text_composition'),    -- 116
    ('has_script',               'cross_lingual', 'language_name',      'text_composition'),    -- 117  (target = ISO 15924 4-letter script code as text_composition)
    ('has_region',               'cross_lingual', 'language_name',      'text_composition'),    -- 118  (target = ISO 3166-1 alpha-2 region code as text_composition)
    ('has_encoding_position',    'unicode',       'codepoint',          'text_composition'),    -- 119  (target = byte sequence in encoding's space as text_composition)
    ('has_ideographic_variant_in_collection', 'unicode', 'codepoint',   'text_composition'),    -- 120  (target = collection-qualified variant glyph identifier as text_composition)
    -- ── AP-8 unified-Glicko-surface migration edges ────────────────────
    -- POS / morph / deprel / lexname / language classifications attest on
    -- the unified substrate.edge_significance surface via these typed
    -- edges. Junction tables (entity_pos, pattern_deprel, etc.) remain as
    -- denormalized analytics caches; authoritative consensus lives here.
    ('has_pos',                  'structural',    'word_form',          'text_composition'),    -- 121  (target = POS category name "NOUN"/"VERB"/etc. as text_composition)
    ('has_morph_feature',        'structural',    'word_form',          'text_composition'),    -- 122  (target = "Gender=Masc"/"Number=Sing"/etc. as text_composition)
    ('has_deprel_pattern',       'structural',    'word_form',          'text_composition'),    -- 123  (target = dep relation "nsubj"/"obj"/etc. as text_composition)
    ('has_lexname',              'structural',    'synset',             'text_composition'),    -- 124  (target = WordNet lexname "noun.animal"/etc. as text_composition)
    ('has_language',             'cross_lingual', NULL,                 'language_name'),       -- 125  (polymorphic source — any entity that asserts a language tag)
    -- ── Generic classification attestation (Gate 1 #38 refactor 2026-05-19) ─
    -- Collapses per-dimension classification edge proliferation into a single
    -- polymorphic edge. Source = any classifiable content entity (codepoint,
    -- word_form, lemma, synset, ...). Target = content-hashed classification
    -- entity whose entity_type discriminates the dimension (general_category /
    -- script / block / bidi_class / east_asian_width / break_property / pos /
    -- lexname / morph_feature / deprel / language_name / ...). Arena routing
    -- by (edge_type × target_entity_type) per AP-30/AP-38 collapse principle —
    -- discrimination via (target_type × provenance × arena), not via
    -- per-dimension edge_type proliferation.
    --
    -- Migrating existing has_pos / has_lexname / has_morph_feature /
    -- has_deprel_pattern edges onto this generic kind is staged for a
    -- follow-up; both surfaces will coexist transitionally until the
    -- migration completes.
    ('has_classification',       'structural',    NULL,                 NULL),                  -- 126
    -- Recipe linking edges (substrate-content recipes per alt-phase flow).
    -- recipe_for_model_source: SafetensorsDecomposer emits at end of
    -- ingest. Source = recipe entity (auto-derived from observed config +
    -- tensor shapes), target = NULL (provenance_id on substrate.edge
    -- already carries the model_source identity). Resolves
    -- --recipe-from-model <code> → recipe entity_hash.
    -- recipe_derived_from: practitioner-fork → parent recipe lineage.
    ('recipe_for_model_source',  'structural',    'recipe',             NULL),                  -- 127
    ('recipe_derived_from',      'structural',    'recipe',             'recipe')               -- 128
) AS s(code, category, source_code, target_code)
LEFT JOIN substrate.entity_type src ON src.code = s.source_code
LEFT JOIN substrate.entity_type tgt ON tgt.code = s.target_code;
