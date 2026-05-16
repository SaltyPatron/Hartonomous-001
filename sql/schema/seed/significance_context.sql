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
    ('sequence_following');
