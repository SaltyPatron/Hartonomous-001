-- 0042_model_analysis_entity_and_edge_types.down.sql

DELETE FROM substrate.edge_type WHERE code IN (
    'has_sparsity_profile',
    'has_weight_distribution',
    'has_activation_range',
    'has_moe_routing',
    'has_spectrum',
    'has_eigenvalue_spectrum',
    'encodes_archetype',
    'has_layer_similarity',
    'has_codebook',
    'contains_codevector'
);

DELETE FROM substrate.entity_type WHERE code IN (
    'sparsity_profile',
    'weight_distribution',
    'activation_range',
    'moe_routing_profile',
    'svd_spectrum',
    'eigenvalue_spectrum',
    'attention_archetype',
    'layer_similarity_pair',
    'codec_codebook',
    'codec_codevector'
);
