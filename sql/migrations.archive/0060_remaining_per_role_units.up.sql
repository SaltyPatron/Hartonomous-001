-- 0060_remaining_per_role_units.up.sql
--
-- Per-role unit emission for the remaining Track-2 transformation tensor
-- families: RoPE freq tables, conv kernels, vision feature directions,
-- modality basis vectors, DETR object query slots, classification heads,
-- bbox heads, LoRA components, diffusion blocks, conformer layers, and
-- audio codec filters. Per the corrected build plan P5: each tensor row
-- (or filter / slot / component) IS the learned function of one unit;
-- decomposing into row-entities + typed edges with significance is the
-- substrate's encoding of the model's inference behavior.
--
-- All hashes are BLAKE3 over f64-canonical content of the unit only —
-- placement (which model, layer, slot, channel, rank) lives on edges
-- and substrate.sequence ordinals, never in the entity hash. Same unit
-- across models collapses to ONE entity → cross-model Glicko-2
-- corroboration on shared learned functions.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('rope_freq_table',          'model_weights'),
    ('object_query_slot',        'model_weights'),
    ('class_projection',         'model_weights'),
    ('bbox_projection',          'model_weights'),
    ('vision_feature_direction', 'model_weights'),
    ('modality_basis_vector',    'model_weights'),
    ('lora_component',           'model_weights'),
    ('conv_filter',              'model_weights'),
    ('diffusion_component',      'model_weights'),
    ('conformer_component',      'model_weights'),
    ('audio_codec_filter',       'model_weights')
    ON CONFLICT (code) DO NOTHING;

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_rope_freqs', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'rope_freq_table')),
    ('has_object_query', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'object_query_slot')),
    ('has_class_projection', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'class_projection')),
    ('has_bbox_projection', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'bbox_projection')),
    ('has_vision_feature', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'vision_feature_direction')),
    ('has_modality_basis', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'modality_basis_vector')),
    ('has_lora_component', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lora_component')),
    ('has_conv_filter', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'conv_filter')),
    ('has_diffusion_component', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'diffusion_component')),
    ('has_conformer_component', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'conformer_component')),
    ('has_codec_filter', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'audio_codec_filter'))
    ON CONFLICT (code) DO NOTHING;
