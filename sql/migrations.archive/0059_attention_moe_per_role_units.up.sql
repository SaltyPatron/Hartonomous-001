-- 0059_attention_moe_per_role_units.up.sql
--
-- Per-role unit emission for the attention and MoE transformation tensor
-- families. Per the corrected build plan P5: each tensor's row IS the
-- learned function of one component; decomposing the tensor into row-
-- entities + typed edges with significance is the substrate's encoding
-- of the model's inference behavior.
--
-- attention_component — one entity per row of an AttentionQuery /
--                       AttentionKey / AttentionValue / AttentionOutput
--                       tensor. Hashed by f64-canonical row content only.
--                       Same row across models collapses → cross-model
--                       Glicko-2 corroboration on attention components.
--
-- moe_route_direction — one entity per row of a MoeRouter tensor. Each
--                       row is the direction in residual-stream space
--                       that selects an expert.
--
-- moe_expert_neuron   — one entity per row of MoeExpertGate / MoeExpertUp /
--                       MoeExpertDown tensors. Per-expert FFN neuron rows.
--
-- Edges:
--   has_attention_component — tensor → attention_component
--   has_route_direction     — tensor → moe_route_direction
--   has_moe_neuron          — tensor → moe_expert_neuron
--
-- Placement (which row in which tensor) recorded via substrate.sequence
-- with ordinal_position = row_index. Layer + projection role recoverable
-- from tensor's tensor_tensor_role junction and in_layer edge.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('attention_component', 'model_weights'),
    ('moe_route_direction', 'model_weights'),
    ('moe_expert_neuron',   'model_weights')
    ON CONFLICT (code) DO NOTHING;

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_attention_component', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'attention_component')),
    ('has_route_direction', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'moe_route_direction')),
    ('has_moe_neuron', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'moe_expert_neuron'))
    ON CONFLICT (code) DO NOTHING;
