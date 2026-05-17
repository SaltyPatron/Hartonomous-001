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
    ('library_of_congress',    'authoritative_standard', 100000,  50, NULL,                1.00),
    ('princeton_wordnet',      'academic_curated',        90000, 100, NULL,                1.00),
    ('omwn_consortium',        'academic_consortium',     85000, 100, 'princeton_wordnet', 0.92),
    ('universaldependencies',  'academic_consortium',     85000, 100, NULL,                1.00),
    ('wiktextract',            'community_curated',       70000, 200, NULL,                1.00),
    ('tatoeba',                'community_contributed',   50000, 350, NULL,                1.00),
    ('huggingface_model',      'model_derived',           60000, 350, NULL,                1.00),
    ('system_computed',        'system_computed',         40000, 350, NULL,                1.00),
    ('user_session',           'user_input',              20000, 500, NULL,                1.00),
    -- ISO / IETF / CLDR per-registry provenances (each is a separate publisher; cross-source consensus accumulates per arena)
    ('iso_15924',              'authoritative_standard',  95000, 100, NULL,                1.00),
    ('iso_3166',               'authoritative_standard',  95000, 100, NULL,                1.00),
    ('ietf_bcp47',             'authoritative_standard',  90000, 100, NULL,                1.00),
    ('cldr',                   'authoritative_standard',  70000, 200, NULL,                1.00),
    -- Encoding-standard provenances — each cross-encoding mapping attests independently
    ('ascii',                  'authoritative_standard',  95000, 100, NULL,                1.00),
    ('iso_8859_1',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_2',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_3',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_4',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_5',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_6',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_7',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_8',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_9',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_10',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_11',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_13',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_14',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_15',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_16',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('windows_1250',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1251',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1252',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1253',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1254',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1255',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1256',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1257',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1258',           'model_derived',           65000, 250, NULL,                1.00),
    ('ebcdic_037',             'authoritative_standard',  80000, 200, NULL,                1.00),
    ('ebcdic_500',             'authoritative_standard',  80000, 200, NULL,                1.00),
    ('ebcdic_1047',            'authoritative_standard',  80000, 200, NULL,                1.00),
    ('koi8_r',                 'authoritative_standard',  85000, 200, NULL,                1.00),
    ('koi8_u',                 'authoritative_standard',  85000, 200, NULL,                1.00),
    ('gb18030',                'authoritative_standard',  95000, 100, NULL,                1.00),
    ('jis_x_0201',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('jis_x_0208',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('jis_x_0212',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('shift_jis',              'authoritative_standard',  85000, 200, NULL,                1.00),
    ('euc_jp',                 'authoritative_standard',  85000, 200, NULL,                1.00),
    ('euc_kr',                 'authoritative_standard',  85000, 200, NULL,                1.00),
    ('big5',                   'authoritative_standard',  85000, 200, NULL,                1.00),
    ('mac_roman',              'model_derived',           60000, 300, NULL,                1.00),
    -- IVD collection provenances (5 collections per UTS #37)
    ('ivd_adobe_japan1',       'authoritative_standard',  85000, 150, NULL,                1.00),
    ('ivd_hanyo_denshi',       'authoritative_standard',  85000, 150, NULL,                1.00),
    ('ivd_krname',             'authoritative_standard',  85000, 150, NULL,                1.00),
    ('ivd_moji_joho',          'authoritative_standard',  85000, 150, NULL,                1.00),
    ('ivd_msarg',              'authoritative_standard',  85000, 150, NULL,                1.00),
    -- Unihan per-language reading provenances
    ('unihan_kmandarin',       'authoritative_standard',  90000, 150, NULL,                1.00),
    ('unihan_kcantonese',      'authoritative_standard',  90000, 150, NULL,                1.00),
    ('unihan_kjapanese',       'authoritative_standard',  90000, 150, NULL,                1.00),
    ('unihan_kvietnamese',     'authoritative_standard',  90000, 150, NULL,                1.00);

-- Modality authority per source — one junction row per (provenance, modality).
INSERT INTO substrate.provenance_modality (provenance_id, modality_code)
SELECT p.id, m.modality_code
  FROM substrate.provenance p
  JOIN (
      VALUES
        ('unicode_consortium',     'text'::substrate.modality_code),
        ('sil_international',      'text'::substrate.modality_code),
        ('library_of_congress',    'text'::substrate.modality_code),
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
