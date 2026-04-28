-- 0017_iso639_seed.up.sql
-- Edge types and entity types for full ISO 639 decomposition.

-- Alternative language names (from Name_Index) are also language_name entities.
-- No new entity type needed — they're the same type with different content.

-- Edge types for language relationships:
-- macrolanguage_contains: zho (Chinese macrolanguage) → cmn (Mandarin individual)
-- has_alternate_name: language_name entity → alternate language_name entity
-- superseded_by: retired code's entity → replacement code's entity (provenance chain)
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('macrolanguage_contains', 'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'language_name'),
        (SELECT id FROM substrate.entity_type WHERE code = 'language_name')),
    ('has_alternate_name', 'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'language_name'),
        (SELECT id FROM substrate.entity_type WHERE code = 'language_name')),
    ('superseded_by', 'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'language_name'),
        (SELECT id FROM substrate.entity_type WHERE code = 'language_name'));
