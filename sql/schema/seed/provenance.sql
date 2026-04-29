-- substrate.provenance seed — wide-band tier ladder.
--
-- Glicko-2 priors span 20K (user_session) to 100K (authoritative_standard).
-- modality_codes enumerates which modalities each source is authoritative
-- in. derives_from + derivation_decay model lineage (OMW = 0.92 × WordNet).
--
-- Tier ladder rationale: cross-modal cross-source comparison only works
-- when a source's prior reflects its actual epistemic status. Flat 1500
-- priors made A* over arenas degenerate to uniform-cost BFS — the
-- topology was structurally absent from the substrate.
INSERT INTO substrate.provenance
    (code, curator_class, initial_mu, initial_sigma, modality_codes, derives_from, derivation_decay)
VALUES
    ('unicode_consortium',     'authoritative_standard', 100000,  50, ARRAY['text'],                                                NULL,                1.00),
    ('sil_international',      'authoritative_standard', 100000,  50, ARRAY['text'],                                                NULL,                1.00),
    ('princeton_wordnet',      'academic_curated',        90000, 100, ARRAY['text'],                                                NULL,                1.00),
    ('omwn_consortium',        'academic_consortium',     85000, 100, ARRAY['text'],                                                'princeton_wordnet', 0.92),
    ('universaldependencies',  'academic_consortium',     85000, 100, ARRAY['text'],                                                NULL,                1.00),
    ('wiktextract',            'community_curated',       70000, 200, ARRAY['text'],                                                NULL,                1.00),
    ('tatoeba',                'community_contributed',   50000, 350, ARRAY['text','audio'],                                        NULL,                1.00),
    ('huggingface_model',      'model_derived',           60000, 350, ARRAY['text','model_weights'],                                NULL,                1.00),
    ('system_computed',        'system_computed',         40000, 350, ARRAY['text','image','audio','video','model_weights'],        NULL,                1.00),
    ('user_session',           'user_input',              20000, 500, ARRAY['text','image','audio','video','model_weights'],        NULL,                1.00);
