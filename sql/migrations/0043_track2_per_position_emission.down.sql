-- 0043_track2_per_position_emission.down.sql
DELETE FROM substrate.edge_type WHERE code = 'has_rank_component';
DELETE FROM substrate.entity_type WHERE code = 'svd_rank_component';
