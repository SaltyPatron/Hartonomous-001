-- substrate.provenance seed — wide-band tier ladder.
--
-- Glicko-2 priors span 20K (user_session) to 100K (authoritative_standard).
-- Modality authority lives in substrate.provenance_modality (junction table
-- with composite PK + bidirectional indexes — no array columns in
-- substrate.*). derives_from + derivation_decay model lineage (OMW = 0.92
-- × WordNet).
--
-- Tier ladder rationale: cross-modal cross-source comparison only works
-- when a source's prior reflects its actual epistemic status. Flat 1500
-- priors made A* over arenas degenerate to uniform-cost BFS — the
-- topology was structurally absent from the substrate.
INSERT INTO substrate.provenance
    (code, curator_class, initial_mu, initial_sigma, derives_from, derivation_decay)
VALUES
    ('unicode_consortium',     'authoritative_standard', 100000,  50, NULL,                1.00),
    ('sil_international',      'authoritative_standard', 100000,  50, NULL,                1.00),
    ('princeton_wordnet',      'academic_curated',        90000, 100, NULL,                1.00),
    ('omwn_consortium',        'academic_consortium',     85000, 100, 'princeton_wordnet', 0.92),
    ('universaldependencies',  'academic_consortium',     85000, 100, NULL,                1.00),
    ('wiktextract',            'community_curated',       70000, 200, NULL,                1.00),
    ('tatoeba',                'community_contributed',   50000, 350, NULL,                1.00),
    ('huggingface_model',      'model_derived',           60000, 350, NULL,                1.00),
    ('system_computed',        'system_computed',         40000, 350, NULL,                1.00),
    ('user_session',           'user_input',              20000, 500, NULL,                1.00);

-- Modality authority per source — one junction row per (provenance, modality).
INSERT INTO substrate.provenance_modality (provenance_id, modality_code)
SELECT p.id, m.modality_code
  FROM substrate.provenance p
  JOIN (
      VALUES
        ('unicode_consortium',     'text'::substrate.modality_code),
        ('sil_international',      'text'::substrate.modality_code),
        ('princeton_wordnet',      'text'::substrate.modality_code),
        ('omwn_consortium',        'text'::substrate.modality_code),
        ('universaldependencies',  'text'::substrate.modality_code),
        ('wiktextract',            'text'::substrate.modality_code),
        ('tatoeba',                'text'::substrate.modality_code),
        ('tatoeba',                'audio'::substrate.modality_code),
        ('huggingface_model',      'text'::substrate.modality_code),
        ('huggingface_model',      'model_weights'::substrate.modality_code),
        ('system_computed',        'text'::substrate.modality_code),
        ('system_computed',        'image'::substrate.modality_code),
        ('system_computed',        'audio'::substrate.modality_code),
        ('system_computed',        'video'::substrate.modality_code),
        ('system_computed',        'model_weights'::substrate.modality_code),
        ('user_session',           'text'::substrate.modality_code),
        ('user_session',           'image'::substrate.modality_code),
        ('user_session',           'audio'::substrate.modality_code),
        ('user_session',           'video'::substrate.modality_code),
        ('user_session',           'model_weights'::substrate.modality_code)
  ) AS m(code, modality_code)
    ON p.code = m.code;
