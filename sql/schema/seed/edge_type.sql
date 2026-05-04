-- 111 edge types. Codes 1..39 land in named partitions
-- (tables/core/edge_structural.sql, edge_cross_lingual.sql, edge_cross_modal.sql,
-- edge_unicode.sql, edge_model.sql); codes 40..111 land in edge_default.
-- Single INSERT...SELECT pattern: tuples in a VALUES CTE, resolved against
-- substrate.entity_type via JOIN once, semantic_weight derived in CASE.
-- NULL source/target codes mean polymorphic.

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
    -- Structural ──────────────────────────────────────────────────────
    ('has_sense',                'structural',    'lemma',              'synset'),
    ('has_form',                 'structural',    'lemma',              'word_form'),
    ('has_lemma',                'structural',    'word_form',          'lemma'),
    ('has_morpheme',             'structural',    'word_form',          'morpheme'),
    ('has_gloss',                'structural',    'synset',             'text_composition'),
    ('has_example',              'structural',    'synset',             'text_composition'),
    ('has_name',                 'structural',    'model_architecture', 'text_composition'),
    ('inflection_of',            'structural',    'word_form',          'lemma'),
    ('has_etymology',            'structural',    'lemma',              'text_composition'),
    ('has_pronunciation',        'structural',    'lemma',              'text_composition'),
    ('has_hyphenation',          'structural',    'lemma',              'text_composition'),
    ('has_wikidata',             'structural',    'lemma',              'text_composition'),
    ('lexicalized_compound',     'structural',    'word_form',          'word_form'),
    ('has_frame',                'structural',    'lemma',              'text_composition'),
    ('has_wordnet_offset',       'structural',    'synset',             'text_composition'),
    -- Cross-lingual ───────────────────────────────────────────────────
    ('aligned_to_synset',        'cross_lingual', 'lemma',              'synset'),
    ('translation_of',           'cross_lingual', 'lemma',              'lemma'),
    ('translation_link',         'cross_lingual', 'text_composition',   'text_composition'),
    ('macrolanguage_contains',   'cross_lingual', 'language_name',      'language_name'),
    ('has_alternate_name',       'cross_lingual', 'language_name',      'language_name'),
    ('superseded_by',            'cross_lingual', 'language_name',      'language_name'),
    ('etym_inherited_from',      'cross_lingual', 'lemma',              'lemma'),
    ('etym_derived_from',        'cross_lingual', 'lemma',              'lemma'),
    ('etym_borrowed_from',       'cross_lingual', 'lemma',              'lemma'),
    ('etym_cognate_with',        'cross_lingual', 'lemma',              'lemma'),
    ('etym_calque_of',           'cross_lingual', 'lemma',              'lemma'),
    ('etym_mention',             'cross_lingual', 'lemma',              'lemma'),
    ('etym_link',                'cross_lingual', 'lemma',              'text_composition'),
    ('etym_etymon',              'cross_lingual', 'lemma',              'lemma'),
    -- Cross-modal ─────────────────────────────────────────────────────
    ('recording_of',             'cross_modal',   'audio_recording',    'text_composition'),
    ('has_contributor',          'cross_modal',   'audio_recording',    'text_composition'),
    -- Unicode ─────────────────────────────────────────────────────────
    ('maps_to_lowercase',        'unicode',       'codepoint',          'codepoint'),
    ('case_folds_to',            'unicode',       'codepoint',          'codepoint'),
    ('has_collation_weight',     'unicode',       'codepoint',          'collation_element'),
    -- Model-derived: architecture metadata ────────────────────────────
    ('in_model',                 'model_derived', 'tensor',             'model_architecture'),
    ('in_layer',                 'model_derived', 'tensor',             'model_architecture'),
    ('has_dtype',                'model_derived', 'tensor',             'text_composition'),
    ('has_shape',                'model_derived', 'tensor',             'text_composition'),
    ('has_hidden_size',          'model_derived', 'model_architecture', 'text_composition'),
    ('has_num_layers',           'model_derived', 'model_architecture', 'text_composition'),
    ('has_num_attention_heads',  'model_derived', 'model_architecture', 'text_composition'),
    ('has_vocab_size',           'model_derived', 'model_architecture', 'text_composition'),
    ('has_token_id',             'model_derived', 'word_form',          'text_composition'),
    ('in_vocabulary',            'model_derived', 'word_form',          'model_architecture'),
    ('co_occurrence',            'model_derived', NULL,                 NULL),
    ('has_tensor',               'model_derived', 'model_architecture', 'tensor'),
    ('has_architecture_name',    'model_derived', 'model_architecture', 'text_composition'),
    -- Model-derived: tensor analysis surfaces ─────────────────────────
    ('has_tensor_name',          'model_derived', 'tensor',             'text_composition'),
    ('has_tokenizer_model',      'model_derived', 'model_architecture', 'text_composition'),
    ('has_token_in_tokenizer',   'model_derived', 'model_architecture', 'word_form'),
    ('has_weight_distribution',  'model_derived', 'tensor',             'weight_distribution'),
    ('has_spectrum',             'model_derived', 'tensor',             'svd_spectrum'),
    ('has_eigenvalue_spectrum',  'model_derived', 'tensor',             'eigenvalue_spectrum'),
    ('has_sparsity_profile',     'model_derived', 'tensor',             'sparsity_profile'),
    ('has_activation_range',     'model_derived', 'tensor',             'activation_range'),
    ('has_layer_norm_scale',     'model_derived', 'tensor',             'layer_norm_scale'),
    ('has_codebook',             'model_derived', 'tensor',             'codec_codebook'),
    ('contains_codevector',      'model_derived', 'codec_codebook',     'codec_codevector'),
    ('encodes_archetype',        'model_derived', 'tensor',             'archetype'),
    ('has_layer_similarity',     'model_derived', 'tensor',             'layer_similarity_pair'),
    ('has_rope_freqs',           'model_derived', 'tensor',             'rope_freq_table'),
    ('has_rank_component',       'model_derived', 'tensor',             'svd_rank_component'),
    ('has_moe_routing',          'model_derived', 'tensor',             'moe_routing_profile'),
    ('has_embedding_position',   'model_derived', 'tensor',             'embedding_position'),
    ('has_ffn_neuron',           'model_derived', 'tensor',             'ffn_neuron'),
    ('has_logit_projection',     'model_derived', 'tensor',             'logit_projection'),
    ('covers_lemma',             'model_derived', 'word_form',          'lemma'),
    ('has_vocab_coverage',       'model_derived', 'tokenizer_model',    'vocab_coverage_profile'),
    -- Model-derived: per-role-unit binding edges ─────────────────────
    ('has_attention_component',  'model_derived', 'tensor',             'attention_pattern'),
    ('has_codec_filter',         'model_derived', 'tensor',             'audio_codec_filter'),
    ('has_bbox_projection',      'model_derived', 'tensor',             'bbox_projection'),
    ('has_class_projection',     'model_derived', 'tensor',             'class_projection'),
    ('has_conformer_component',  'model_derived', 'tensor',             'conformer_component'),
    ('has_conv_filter',          'model_derived', 'tensor',             'conv_filter'),
    ('has_diffusion_component',  'model_derived', 'tensor',             'diffusion_component'),
    ('has_lora_component',       'model_derived', 'tensor',             'lora_component'),
    ('has_modality_basis',       'model_derived', 'tensor',             'modality_basis_vector'),
    ('has_moe_neuron',           'model_derived', 'tensor',             'moe_expert_neuron'),
    ('has_route_direction',      'model_derived', 'tensor',             'moe_route_direction'),
    ('has_object_query',         'model_derived', 'tensor',             'object_query_slot'),
    ('has_vision_feature',       'model_derived', 'tensor',             'vision_feature_direction'),
    -- Semantic: WordNet pointers (synset ↔ synset) ────────────────────
    ('hypernym',                 'semantic',      'synset', 'synset'),
    ('hyponym',                  'semantic',      'synset', 'synset'),
    ('instance_hypernym',        'semantic',      'synset', 'synset'),
    ('instance_hyponym',         'semantic',      'synset', 'synset'),
    ('member_holonym',           'semantic',      'synset', 'synset'),
    ('substance_holonym',        'semantic',      'synset', 'synset'),
    ('part_holonym',             'semantic',      'synset', 'synset'),
    ('member_meronym',           'semantic',      'synset', 'synset'),
    ('substance_meronym',        'semantic',      'synset', 'synset'),
    ('part_meronym',             'semantic',      'synset', 'synset'),
    ('attribute',                'semantic',      'synset', 'synset'),
    ('derivationally_related',   'semantic',      'synset', 'synset'),
    ('antonym',                  'semantic',      'synset', 'synset'),
    ('similar_to',               'semantic',      'synset', 'synset'),
    ('also_see',                 'semantic',      'synset', 'synset'),
    ('verb_group',               'semantic',      'synset', 'synset'),
    ('entailment',               'semantic',      'synset', 'synset'),
    ('cause',                    'semantic',      'synset', 'synset'),
    ('participle_of_verb',       'semantic',      'synset', 'synset'),
    ('pertainym',                'semantic',      'synset', 'synset'),
    ('domain_of_synset_topic',   'semantic',      'synset', 'synset'),
    ('member_of_domain_topic',   'semantic',      'synset', 'synset'),
    ('domain_of_synset_region',  'semantic',      'synset', 'synset'),
    ('member_of_domain_region',  'semantic',      'synset', 'synset'),
    ('domain_of_synset_usage',   'semantic',      'synset', 'synset'),
    ('member_of_domain_usage',   'semantic',      'synset', 'synset'),
    -- Semantic: Wiktionary lemma ↔ lemma ──────────────────────────────
    ('synonym',                  'semantic',      'lemma',  'lemma'),
    ('coordinate_term',          'semantic',      'lemma',  'lemma'),
    ('derived',                  'semantic',      'lemma',  'lemma'),
    ('related',                  'semantic',      'lemma',  'lemma')
) AS s(code, category, source_code, target_code)
LEFT JOIN substrate.entity_type src ON src.code = s.source_code
LEFT JOIN substrate.entity_type tgt ON tgt.code = s.target_code;
