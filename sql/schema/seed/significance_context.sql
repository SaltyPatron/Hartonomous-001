-- 10 starter arenas. The substrate's significance_context is open vocabulary —
-- new arenas can be inserted at runtime; significance must auto-prime against
-- every arena in this table at the time of insert (rule 45 AP-1).
INSERT INTO substrate.significance_context (code) VALUES
    ('lexical_disambiguation'),
    ('syntactic_role_fitness'),
    ('translation_quality'),
    ('model_trust'),
    ('source_authority'),
    ('semantic_relevance'),
    ('corroboration_strength'),
    ('frequency_significance'),
    ('attention_pattern_confidence'),
    ('morphological_productivity'),
    -- Bigram next-token prior arena. Populated by
    -- substrate.populate_sequence_following_edges from content trajectory
    -- ordinals. Source of generative coherence at inference time.
    ('sequence_following'),
    -- Unicode/ISO/CLDR/encoding cross-source consensus arenas (per
    -- universal-cross-source-attestation framing). Each names a contested
    -- surface where multiple sources fire attestation events on shared
    -- content-addressed edge identities.
    ('unicode_version_consensus'),         -- 30 UCD versions attesting per-cp properties
    ('encoding_position_consensus'),       -- ASCII/ISO 8859/EBCDIC/Windows/JIS/GB/etc.
    ('ivd_collection_consensus'),          -- 5 IVD collections attesting ideographic variants
    ('unihan_reading_consensus'),          -- 4 Unihan reading languages
    ('consortium_discussion_density'),     -- L2/IRG/WG2 working docs (future scope)
    ('script_membership_consensus'),       -- Unicode + ISO 15924 + CLDR + corpus attestation
    ('language_codepoint_coverage_consensus'), -- per-language codepoint usage
    ('locale_definition_consensus');       -- per-CLDR-version locale definition stability
