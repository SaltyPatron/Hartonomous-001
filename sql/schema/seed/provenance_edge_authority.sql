-- substrate.provenance_edge_authority seed — specialty (source × edge_type) μ overrides.
--
-- The default prior μ = p.initial_mu × et.semantic_weight × p.derivation_decay
-- works for most cases. These rows override the default for specialty
-- combinations where the source's authority for a specific edge-kind is
-- much stronger or weaker than the multiplicative product would yield.

-- Wiktionary IS the etymology / pronunciation / hyphenation authority,
-- regardless of its 70k base trust.
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 95000,  80
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'wiktextract' AND et.code = 'has_etymology'
ON CONFLICT DO NOTHING;
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 95000,  80
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'wiktextract' AND et.code = 'has_pronunciation'
ON CONFLICT DO NOTHING;
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 90000, 100
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'wiktextract' AND et.code = 'has_hyphenation'
ON CONFLICT DO NOTHING;

-- WordNet has etymology / pronunciation but they're weak, not its specialty —
-- explicit knock-down overrides the multiplicative default.
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 20000, 500
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'princeton_wordnet' AND et.code = 'has_etymology'
ON CONFLICT DO NOTHING;
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 15000, 600
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'princeton_wordnet' AND et.code = 'has_pronunciation'
ON CONFLICT DO NOTHING;

-- Tatoeba IS the bilingual sentence-pair and audio authority.
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 85000, 100
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'tatoeba' AND et.code = 'translation_link'
ON CONFLICT DO NOTHING;
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 85000, 100
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'tatoeba' AND et.code = 'recording_of'
ON CONFLICT DO NOTHING;
