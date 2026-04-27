-- 0063_substrate_edge_decomposition.down.sql

DELETE FROM substrate.edge_type WHERE code IN ('ffn_input_edge', 'ffn_output_edge');
DELETE FROM substrate.entity_type WHERE code = 'residual_direction';
