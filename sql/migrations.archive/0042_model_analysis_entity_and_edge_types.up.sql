-- 0042_model_analysis_entity_and_edge_types.up.sql
--
-- Registers the entity_type + edge_type rows that the safetensors analysis
-- passes have always emitted but that were never seeded.
--
-- Without these rows the passes throw `Unknown entity_type code: 'X'` /
-- `Unknown edge_type code: 'X'` the moment they try to write their first batch.
-- All ten passes (sparsity, weight_distribution, activation_range, moe_routing,
-- svd, eigenvalues, attention_archetype, layer_similarity, codec_analysis,
-- text_artifacts) ran zero analysis output as a result, leaving the model side
-- of the substrate to nothing but tensor entities + has_tensor edges.
--
-- New entity types (route to substrate.entity_default per 0006 partition layout):
--   sparsity_profile, weight_distribution, activation_range, moe_routing_profile,
--   svd_spectrum, eigenvalue_spectrum, attention_archetype, layer_similarity_pair,
--   codec_codebook, codec_codevector
--
-- New edge types (route to substrate.edge_default per 0006 partition layout):
--   has_sparsity_profile, has_weight_distribution, has_activation_range,
--   has_moe_routing, has_spectrum, has_eigenvalue_spectrum, encodes_archetype,
--   has_layer_similarity, has_codebook, contains_codevector

-- ── New entity types (modality = model_weights) ──────────────────────────
INSERT INTO substrate.entity_type (code, modality) VALUES
    ('sparsity_profile',      'model_weights'),
    ('weight_distribution',   'model_weights'),
    ('activation_range',      'model_weights'),
    ('moe_routing_profile',   'model_weights'),
    ('svd_spectrum',          'model_weights'),
    ('eigenvalue_spectrum',   'model_weights'),
    ('attention_archetype',   'model_weights'),
    ('layer_similarity_pair', 'model_weights'),
    ('codec_codebook',        'model_weights'),
    ('codec_codevector',      'model_weights');

-- ── New edge types ───────────────────────────────────────────────────────
-- All sourced from a `tensor` entity (or `model_architecture` for cross-tensor
-- pairs). Categorized as model_derived where the target is a derived analysis
-- entity, structural where target is purely descriptive metadata. The category
-- distinction here doesn't change partition routing (both fall to edge_default
-- for new ids) but keeps the taxonomy honest for future partition planning.

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_sparsity_profile',     'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'sparsity_profile')),
    ('has_weight_distribution',  'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'weight_distribution')),
    ('has_activation_range',     'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'activation_range')),
    ('has_moe_routing',          'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'moe_routing_profile')),
    ('has_spectrum',             'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'svd_spectrum')),
    ('has_eigenvalue_spectrum',  'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'eigenvalue_spectrum')),
    ('encodes_archetype',        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'attention_archetype')),
    ('has_layer_similarity',     'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'layer_similarity_pair')),
    ('has_codebook',             'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'codec_codebook')),
    ('contains_codevector',      'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'codec_codebook'),
        (SELECT id FROM substrate.entity_type WHERE code = 'codec_codevector'));
