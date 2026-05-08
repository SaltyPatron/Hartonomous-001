-- 14 starter attestation types. Open vocabulary — runtime additions are
-- expected (e.g., per-corpus or per-model arena-attestation pairs).
--
-- Per-event weight defaults reflect evidence density vs confidence:
--   curated lexical relations: 1.0 (one high-confidence event)
--   corpus co-occurrence: 0.1  (high-volume, low-per-event-confidence)
--   model attention/circuit: 0.5 (medium-volume, structural-confidence)
--   inference outcomes:    1.5 (sparse, ground-truth signal)
--   expert correction:     2.0 (highest single-event impact)
--
-- These are PRIORS. Per-emission weight overrides are passed through the
-- significance-event API at call time.
INSERT INTO substrate.attestation_type (code, description, default_event_weight) VALUES
    ('corpus_co_occurrence_window',
     'Decomposer slid window of radius R over a parent text composition; per-pair weighted comparison event. Weight scaled by 1/distance × parent_significance × 1/RLE_count. Substrate analog of word2vec/GloVe statistics.',
     0.1),
    ('corpus_proximity_within_sentence',
     'Same as corpus_co_occurrence_window but strictly confined within a sentence boundary (no cross-sentence pairs). Used when sentence-level decomposition is the natural unit.',
     0.1),
    ('lexical_curated_relation',
     'Curated lexicon assertion (WordNet has_sense, Wiktionary etymology, OMW alignment, UD deprel labels). High per-event confidence because hand-curated.',
     1.0),
    ('lexical_attested_translation',
     'Bilingual lexicon entry or aligned-sentence translation pair (Tatoeba, OPUS). One attestation per parallel pair.',
     0.8),
    ('model_embedding_proximity',
     'Cosine/magnitude of two tokens'' rows in a decomposed model''s embedding or unembedding matrix. Track-1 firefly geometry binding.',
     0.4),
    ('model_attention_pattern',
     'Attention head''s Q×K projection peak between two existing token entities. Track-2 per-role-unit attestation expressed as a direct token↔token edge.',
     0.5),
    ('model_ffn_factor_alignment',
     'FFN per-role unit''s input/output projection alignment with two existing token entities. Track-2 attestation.',
     0.5),
    ('model_per_role_unit_circuit',
     'Identified circuit binding per-role units (substrate entities) to a relation between existing token entities. Bridge edges queries_from/attends_to_class/projects_to.',
     0.6),
    ('cross_model_corroboration',
     'Voronoi-cell tightness or Fréchet-trajectory similarity between per-role units across two or more decomposed models. Cross-architecture consensus event.',
     0.7),
    ('cross_model_divergence',
     'Cross-model fireflies disagree; cell fragmented. Recorded as negative-evidence event so Glicko sigma stays wide and the engine''s curiosity loop targets the gap.',
     0.5),
    ('inference_outcome_accept',
     'Step 6 of inference: query path produced an answer the user/downstream-task accepted. Updates the path''s edge_significance positively. Closes the OODA loop.',
     1.5),
    ('inference_outcome_reject',
     'Step 6: query path produced an answer that was rejected. Updates the path''s edge_significance negatively (loss event).',
     1.5),
    ('expert_correction',
     'Human-in-loop override of an edge''s rating. Highest per-event weight; used sparingly for corrections that should dominate accumulated automatic evidence.',
     2.0),
    ('provenance_authority_corroboration',
     'Multi-source assertion resolved through provenance_edge_authority. Used when several provenances of differing trust priors agree on an edge''s rating.',
     0.8);
