-- Stage 0023: backfill the per-role-unit entity_types and edge_types that
-- the model-decomposition analysis-pass DAG references but that 0005's
-- reference seed never emitted. Symptom on an unmigrated DB:
--   System.InvalidOperationException: Unknown edge_type code: 'has_attention_component'
--      at CodeResolver.Resolve(...)
--   thrown from StreamingIngestionPipeline.SubmitBatchAsync on the first
--   batch of every model-decomposition pass other than embedding_position
--   and ffn_neuron. AttentionComponentPass, AudioCodecFilterPass,
--   BboxHeadPass, ClassHeadPass, ConformerComponentPass, ConvFilterPass,
--   DiffusionComponentPass, LoraComponentPass, ModalityBasisPass,
--   MoeExpertNeuronPass, MoeRouteDirectionPass, ObjectQueryPass, and
--   VisionFeaturePass all hit this path.
--
-- The schema seed files (sql/schema/seed/entity_type.sql and edge_type.sql)
-- have been updated to include these rows for fresh installs. This forward
-- migration brings any DB that already ran 0005 into the same state.
-- ON CONFLICT (code) DO NOTHING makes it idempotent; running it on a fresh
-- DB whose seed file already inserted the rows is a no-op.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('audio_codec_filter',       'model_weights'),
    ('bbox_projection',          'model_weights'),
    ('class_projection',         'model_weights'),
    ('conformer_component',      'model_weights'),
    ('conv_filter',              'model_weights'),
    ('diffusion_component',      'model_weights'),
    ('lora_component',           'model_weights'),
    ('modality_basis_vector',    'model_weights'),
    ('moe_expert_neuron',        'model_weights'),
    ('moe_route_direction',      'model_weights'),
    ('object_query_slot',        'model_weights'),
    ('vision_feature_direction', 'model_weights')
ON CONFLICT (code) DO NOTHING;

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_attention_component',  'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'attention_pattern')),
    ('has_codec_filter',         'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'audio_codec_filter')),
    ('has_bbox_projection',      'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'bbox_projection')),
    ('has_class_projection',     'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'class_projection')),
    ('has_conformer_component',  'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'conformer_component')),
    ('has_conv_filter',          'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'conv_filter')),
    ('has_diffusion_component',  'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'diffusion_component')),
    ('has_lora_component',       'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lora_component')),
    ('has_modality_basis',       'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'modality_basis_vector')),
    ('has_moe_neuron',           'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'moe_expert_neuron')),
    ('has_route_direction',      'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'moe_route_direction')),
    ('has_object_query',         'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'object_query_slot')),
    ('has_vision_feature',       'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'vision_feature_direction'))
ON CONFLICT (code) DO NOTHING;
