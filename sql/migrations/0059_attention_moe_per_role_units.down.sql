-- 0059_attention_moe_per_role_units.down.sql
DELETE FROM substrate.edge_type WHERE code IN
    ('has_attention_component', 'has_route_direction', 'has_moe_neuron');
DELETE FROM substrate.entity_type WHERE code IN
    ('attention_component', 'moe_route_direction', 'moe_expert_neuron');
