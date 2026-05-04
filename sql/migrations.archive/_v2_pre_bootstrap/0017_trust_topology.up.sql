-- Stage 0017: trust topology + tenancy + asserter Glicko-on-edge.
--
-- The substrate's significance system was a four-way schism: decomposers
-- emit entity μ in 50K–95K (wide-band), the seeded provenance.initial_mu
-- sits at 1K–2K (chess-narrow), the substrate.significance_mu domain is
-- FLOAT8 with no constraint and a comment that lies "1000-2000," and the
-- ext/hartonomous_pg domain glicko_mu CHECKs ≤ 4000 (chess hard cap).
-- The mean μ in entity_significance arena 'source_authority' was 91366,
-- the mean μ in edge_significance was 2000 across every arena. Cross-modal
-- cross-source comparison was structurally impossible.
--
-- This migration replaces the flat-prior topology with the multi-axis
-- topology the invention requires:
--
--   trust = f(provenance × modality × content-kind × lineage × asserter × tenant-scope)
--
-- Concretely:
--
--   substrate.provenance gains:
--     modality_codes      TEXT[]   what modalities the source is authoritative in
--     derives_from        TEXT     code of an upstream source whose authority
--                                  this one inherits (with decay)
--     derivation_decay    FLOAT8   how much trust flows through that lineage
--     initial_sigma       FLOAT8   per-source uncertainty (was hardcoded 350)
--     scope_kind          TEXT     'global' | 'tenant' | 'user'
--     scope_entity_type_id INT     when scope_kind ≠ 'global', identifies the
--     scope_entity_hash    BYTEA   tenant or user this provenance belongs to
--
--   substrate.edge_type gains:
--     semantic_weight     FLOAT8   POS/sense/antonym carry higher initial μ
--                                  than 'related' / 'similar_to'
--
--   substrate.provenance_edge_authority (new junction):
--     explicit (provenance, edge_type) overrides for specialty authority
--     (Wiktionary's etymology, etc.) that breaks the default product
--
--   New entity types: 'tenant' and 'user'. They are first-class entities;
--   their substrate.entity_significance per arena IS their reliability
--   score. The same Glicko mechanism rates everything else applies to them.
--   Tenant and user entities content-address by their stable identifiers
--   (domain, email).
--
--   Reseed of substrate.provenance values to the wide-band tier ladder:
--     unicode_consortium     100000 σ=50    [text]
--     sil_international      100000 σ=50    [text]
--     princeton_wordnet       90000 σ=100   [text]
--     omwn_consortium         85000 σ=100   [text]   ← derives_from princeton_wordnet, decay=0.92
--     universaldependencies   85000 σ=100   [text]
--     wiktextract             70000 σ=200   [text]
--     tatoeba                 50000 σ=350   [text, audio]
--     huggingface_model       60000 σ=350   [text, model_weights]
--     system_computed         40000 σ=350   [text, image, audio, video, model_weights]
--     user_session            20000 σ=500   [text, image, audio, video, model_weights]
--
--   Reseed of edge_type.semantic_weight (POS/sense/antonym >> related):
--     1.0   has_sense, has_lemma, has_form, inflection_of, hypernym, hyponym,
--           instance_hypernym, instance_hyponym, antonym
--     0.9   member_holonym, substance_holonym, part_holonym, member_meronym,
--           substance_meronym, part_meronym, has_morpheme
--     0.85  translation_of, aligned_to_synset, translation_link
--     0.7   has_etymology, has_pronunciation, has_hyphenation, has_wikidata
--     0.6   similar_to, also_see, verb_group, attribute, derivationally_related
--     0.5   synonym, related, coordinate_term, derived
--
--   Specialty overrides via substrate.provenance_edge_authority:
--     wiktextract × has_etymology     μ=95000 σ=80   (Wiktionary specialty)
--     wiktextract × has_pronunciation μ=95000 σ=80
--     wiktextract × has_hyphenation   μ=90000 σ=100
--     princeton_wordnet × has_etymology       μ=20000 σ=500 (WordNet has them but weak)
--     princeton_wordnet × has_pronunciation   μ=15000 σ=600
--     tatoeba × translation_link              μ=85000 σ=100 (Tatoeba specialty)
--     tatoeba × recording_of                  μ=85000 σ=100 (Tatoeba audio specialty)
--
--   Function rewrite: substrate.prime_edge_significance_for_staging() now
--   computes μ from the four-product and σ from per-provenance σ, with
--   optional override from provenance_edge_authority.
--
-- After this migration, every edge ingested gets initial μ that respects:
--   - the source's per-modality authority,
--   - the structural value of the edge-kind being asserted,
--   - the source's lineage if any,
--   - and explicit overrides for specialty cases.
-- A* over arenas finally becomes semantically meaningful instead of
-- degenerate-uniform-cost-BFS.

-- ── New entity types: tenant, user ─────────────────────────────────
INSERT INTO substrate.entity_type (code, modality) VALUES
    ('tenant', 'organization'),
    ('user',   'person')
ON CONFLICT (code) DO NOTHING;

-- ── ALTER substrate.provenance ─────────────────────────────────────
ALTER TABLE substrate.provenance
    ADD COLUMN IF NOT EXISTS modality_codes       TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS derives_from         VARCHAR(64),
    ADD COLUMN IF NOT EXISTS derivation_decay     FLOAT8 NOT NULL DEFAULT 1.0,
    ADD COLUMN IF NOT EXISTS initial_sigma        FLOAT8 NOT NULL DEFAULT 350.0,
    ADD COLUMN IF NOT EXISTS scope_kind           TEXT   NOT NULL DEFAULT 'global'
        CHECK (scope_kind IN ('global', 'tenant', 'user')),
    ADD COLUMN IF NOT EXISTS scope_entity_type_id INT,
    ADD COLUMN IF NOT EXISTS scope_entity_hash    BYTEA;

-- self-referential FK after column exists
ALTER TABLE substrate.provenance
    DROP CONSTRAINT IF EXISTS provenance_derives_from_fkey;
ALTER TABLE substrate.provenance
    ADD CONSTRAINT provenance_derives_from_fkey
    FOREIGN KEY (derives_from) REFERENCES substrate.provenance(code);

-- ── ALTER substrate.edge_type ──────────────────────────────────────
ALTER TABLE substrate.edge_type
    ADD COLUMN IF NOT EXISTS semantic_weight FLOAT8 NOT NULL DEFAULT 1.0;

-- ── New junction: provenance_edge_authority ────────────────────────
-- @include schema/tables/junctions/provenance_edge_authority.sql

-- ── Reseed provenance with wide-band tier values + modality + lineage
UPDATE substrate.provenance SET
    initial_mu = 100000, initial_sigma = 50, modality_codes = ARRAY['text']
    WHERE code = 'unicode_consortium';
UPDATE substrate.provenance SET
    initial_mu = 100000, initial_sigma = 50, modality_codes = ARRAY['text']
    WHERE code = 'sil_international';
UPDATE substrate.provenance SET
    initial_mu = 90000,  initial_sigma = 100, modality_codes = ARRAY['text']
    WHERE code = 'princeton_wordnet';
UPDATE substrate.provenance SET
    initial_mu = 85000,  initial_sigma = 100, modality_codes = ARRAY['text'],
    derives_from = 'princeton_wordnet', derivation_decay = 0.92
    WHERE code = 'omwn_consortium';
UPDATE substrate.provenance SET
    initial_mu = 85000,  initial_sigma = 100, modality_codes = ARRAY['text']
    WHERE code = 'universaldependencies';
UPDATE substrate.provenance SET
    initial_mu = 70000,  initial_sigma = 200, modality_codes = ARRAY['text']
    WHERE code = 'wiktextract';
UPDATE substrate.provenance SET
    initial_mu = 50000,  initial_sigma = 350, modality_codes = ARRAY['text', 'audio']
    WHERE code = 'tatoeba';
UPDATE substrate.provenance SET
    initial_mu = 60000,  initial_sigma = 350, modality_codes = ARRAY['text', 'model_weights']
    WHERE code = 'huggingface_model';
UPDATE substrate.provenance SET
    initial_mu = 40000,  initial_sigma = 350,
    modality_codes = ARRAY['text', 'image', 'audio', 'video', 'model_weights']
    WHERE code = 'system_computed';
UPDATE substrate.provenance SET
    initial_mu = 20000,  initial_sigma = 500,
    modality_codes = ARRAY['text', 'image', 'audio', 'video', 'model_weights']
    WHERE code = 'user_session';

-- ── Reseed edge_type semantic_weight ───────────────────────────────
-- Tier 1.0: structural and antonymy — strongest semantic claims
UPDATE substrate.edge_type SET semantic_weight = 1.0 WHERE code IN (
    'has_sense', 'has_lemma', 'has_form', 'inflection_of',
    'hypernym', 'hyponym', 'instance_hypernym', 'instance_hyponym', 'antonym');
-- Tier 0.9: meronymy and morphology
UPDATE substrate.edge_type SET semantic_weight = 0.9  WHERE code IN (
    'member_holonym', 'substance_holonym', 'part_holonym',
    'member_meronym', 'substance_meronym', 'part_meronym', 'has_morpheme');
-- Tier 0.85: cross-lingual structural alignment
UPDATE substrate.edge_type SET semantic_weight = 0.85 WHERE code IN (
    'translation_of', 'aligned_to_synset', 'translation_link');
-- Tier 0.7: linguistic specialty content (etymology, pronunciation, etc.)
UPDATE substrate.edge_type SET semantic_weight = 0.7  WHERE code IN (
    'has_etymology', 'has_pronunciation', 'has_hyphenation', 'has_wikidata');
-- Tier 0.6: looser semantic relations
UPDATE substrate.edge_type SET semantic_weight = 0.6  WHERE code IN (
    'similar_to', 'also_see', 'verb_group', 'attribute', 'derivationally_related');
-- Tier 0.5: weakest semantic claims
UPDATE substrate.edge_type SET semantic_weight = 0.5  WHERE code IN (
    'synonym', 'related', 'coordinate_term', 'derived');

-- ── Specialty overrides: per-(provenance × edge_type) authority ────
-- Wiktionary IS the etymology authority, regardless of its 70k base trust.
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 95000, 80
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'wiktextract' AND et.code = 'has_etymology'
ON CONFLICT DO NOTHING;
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 95000, 80
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'wiktextract' AND et.code = 'has_pronunciation'
ON CONFLICT DO NOTHING;
INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, 90000, 100
  FROM substrate.provenance p, substrate.edge_type et
 WHERE p.code = 'wiktextract' AND et.code = 'has_hyphenation'
ON CONFLICT DO NOTHING;

-- WordNet has etymology / pronunciation but they're weak, not its specialty.
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

-- ── Function rewrite: prime_edge_significance with compound formula
-- @include schema/functions/prime_edge_significance_v2.sql

-- ── Backfill existing edge_significance with the new formula ───────
-- Existing rows from migration 0015 are at flat μ=2000 across every arena
-- (UCD's 41K edges × 10 arenas = 417K rows). Reset them to compound-formula
-- values so A* over arenas reflects the new topology immediately.
-- Only updates rows where games=0 (no comparison events have happened yet).
UPDATE substrate.edge_significance es SET
    mu = COALESCE(
        pea.initial_mu,
        p.initial_mu * et.semantic_weight * p.derivation_decay
    ),
    sigma = COALESCE(pea.initial_sigma, p.initial_sigma)
  FROM substrate.edge e
  JOIN substrate.edge_type  et ON et.id = e.edge_type_id
  JOIN substrate.provenance p  ON p.id  = e.provenance_id
  LEFT JOIN substrate.provenance_edge_authority pea
    ON pea.provenance_id = p.id
   AND pea.edge_type_id  = e.edge_type_id
 WHERE es.edge_type_id = e.edge_type_id
   AND es.edge_hash    = e.hash
   AND es.games        = 0;

-- ── significance_mu domain comment update (the chess-comment lie) ──
COMMENT ON DOMAIN substrate.significance_mu IS
    'Glicko-2 rating mean. Wide-band: trust priors 20K (user) to 100K (authoritative standard); arena-specific overrides via provenance_edge_authority can exceed source defaults. Values evolve via comparison events.';
