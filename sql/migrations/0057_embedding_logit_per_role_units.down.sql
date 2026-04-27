-- 0057_embedding_logit_per_role_units.down.sql
DELETE FROM substrate.edge_type WHERE code IN ('has_embedding_position', 'has_logit_projection');
DELETE FROM substrate.entity_type WHERE code IN ('embedding_position', 'logit_projection');
