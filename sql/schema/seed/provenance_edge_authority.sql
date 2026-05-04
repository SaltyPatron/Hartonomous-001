-- substrate.provenance_edge_authority seed — specialty (source × edge_type) μ overrides.
--
-- One INSERT...SELECT against a VALUES CTE; codes resolve to ids via JOIN once.
-- The default prior μ = p.initial_mu × et.semantic_weight × p.derivation_decay
-- is right for most cases. Rows here override for combinations where source
-- authority on a specific edge-kind diverges from the multiplicative product.

INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, o.initial_mu, o.initial_sigma
  FROM (VALUES
    -- Wiktionary IS the etymology / pronunciation / hyphenation authority.
    ('wiktextract',       'has_etymology',     95000.0,  80.0),
    ('wiktextract',       'has_pronunciation', 95000.0,  80.0),
    ('wiktextract',       'has_hyphenation',   90000.0, 100.0),
    -- WordNet has etymology / pronunciation but they're weak, not its specialty.
    ('princeton_wordnet', 'has_etymology',     20000.0, 500.0),
    ('princeton_wordnet', 'has_pronunciation', 15000.0, 600.0),
    -- Tatoeba IS the bilingual sentence-pair and audio authority.
    ('tatoeba',           'translation_link',  85000.0, 100.0),
    ('tatoeba',           'recording_of',      85000.0, 100.0)
  ) AS o(provenance_code, edge_type_code, initial_mu, initial_sigma)
  JOIN substrate.provenance p  ON p.code  = o.provenance_code
  JOIN substrate.edge_type  et ON et.code = o.edge_type_code
ON CONFLICT (provenance_id, edge_type_id) DO NOTHING;
