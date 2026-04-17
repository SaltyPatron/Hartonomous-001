-- 0017_iso639_seed.down.sql
DELETE FROM substrate.edge_type WHERE code IN (
    'macrolanguage_contains', 'has_alternate_name', 'superseded_by'
);
