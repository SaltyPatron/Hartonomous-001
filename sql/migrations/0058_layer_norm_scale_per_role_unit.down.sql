-- 0058_layer_norm_scale_per_role_unit.down.sql
DELETE FROM substrate.edge_type WHERE code = 'has_layer_norm_scale';
DELETE FROM substrate.entity_type WHERE code = 'layer_norm_scale';
