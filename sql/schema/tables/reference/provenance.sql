-- substrate.provenance — source of an entity or edge with trust prior.
--
-- The provenance row carries the trust topology axes the substrate combines
-- into per-arena Glicko-2 priors:
--
--   trust = f(provenance × modality × content-kind × lineage × asserter × tenant-scope)
--
-- The COALESCE formula in the substrate's edge_significance view (and in
-- pg_traverse_astar's bulk-fetch) computes effective μ from these axes:
--
--   μ₀ = COALESCE(
--          provenance_edge_authority.initial_mu,
--          p.initial_mu × et.semantic_weight × p.derivation_decay
--        )
--
-- initial_mu lives in the wide-band tier ladder (20K user-tier through 100K
-- authoritative-standard); paired with initial_sigma per source. modality_codes
-- enumerates the modalities a source is authoritative in. derives_from +
-- derivation_decay model authority lineage (e.g. OMW = 0.92 × WordNet).
-- scope_kind / scope_entity_* support per-tenant and per-user provenances —
-- these tenant/user provenances point at their entity row in
-- substrate.entity (entity types 'tenant' / 'user').

CREATE TABLE substrate.provenance (
    id                   SERIAL PRIMARY KEY,
    code                 VARCHAR(64) NOT NULL UNIQUE,
    curator_class        VARCHAR(32) NOT NULL,
    initial_mu           FLOAT8      NOT NULL,
    -- Per-source uncertainty for Glicko-2 priors (was hardcoded 350 before
    -- the wide-band tier ladder reseed).
    initial_sigma        FLOAT8      NOT NULL DEFAULT 350.0,
    -- Modalities this source is authoritative in. Empty array → text default.
    modality_codes       TEXT[]      NOT NULL DEFAULT '{}',
    -- Lineage: code of an upstream source whose authority this one inherits.
    derives_from         VARCHAR(64),
    -- Lineage decay factor applied when the parent's trust flows through.
    -- 1.0 = no decay; OMW from princeton_wordnet uses 0.92.
    derivation_decay     FLOAT8      NOT NULL DEFAULT 1.0,
    -- Scope: 'global' (default), 'tenant' (org-scoped), 'user' (user-scoped).
    -- Per-tenant and per-user provenances are first-class — their own
    -- substrate.entity_significance rows are their reliability scores.
    scope_kind           TEXT        NOT NULL DEFAULT 'global'
                                     CHECK (scope_kind IN ('global', 'tenant', 'user')),
    -- When scope_kind ≠ 'global', identifies which tenant/user owns this
    -- provenance via composite handle into substrate.entity.
    scope_entity_type_id INT,
    scope_entity_hash    BYTEA,
    -- Self-referential lineage FK; deferred so seeding can insert in any order.
    CONSTRAINT provenance_derives_from_fkey
        FOREIGN KEY (derives_from) REFERENCES substrate.provenance(code)
        DEFERRABLE INITIALLY DEFERRED
);

COMMENT ON TABLE substrate.provenance IS
    'Source of an entity or edge with trust prior. Carries the trust topology axes (modality, lineage, scope) the substrate combines into per-arena Glicko-2 priors via COALESCE(provenance_edge_authority.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay).';
COMMENT ON COLUMN substrate.provenance.curator_class IS
    'authoritative_standard, academic_curated, academic_consortium, community_curated, community_contributed, model_derived, system_computed, user_input.';
COMMENT ON COLUMN substrate.provenance.initial_mu IS
    'Glicko-2 prior μ. Wide-band ladder: 20K (user_session) → 100K (authoritative_standard). Edge-time prior is multiplied by edge_type.semantic_weight × derivation_decay (with optional provenance_edge_authority override).';
COMMENT ON COLUMN substrate.provenance.modality_codes IS
    'Modalities this source is authoritative in (text, audio, image, video, model_weights). Cross-modal claims fall back to default.';
COMMENT ON COLUMN substrate.provenance.derives_from IS
    'Code of an upstream provenance whose authority this one inherits — together with derivation_decay, models trust lineage (OMW ← princeton_wordnet at 0.92).';
COMMENT ON COLUMN substrate.provenance.scope_kind IS
    'global = system-wide source; tenant = org-scoped; user = user-scoped. Tenant/user provenances are first-class substrate citizens — their entity_significance per arena IS their reliability score.';
