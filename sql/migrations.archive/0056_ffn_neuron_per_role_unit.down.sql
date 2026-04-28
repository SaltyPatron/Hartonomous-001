-- 0056_ffn_neuron_per_role_unit.down.sql
DELETE FROM substrate.edge_type WHERE code = 'has_ffn_neuron';
DELETE FROM substrate.entity_type WHERE code = 'ffn_neuron';
