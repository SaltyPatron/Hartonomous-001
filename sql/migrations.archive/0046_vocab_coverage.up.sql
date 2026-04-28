-- 0046_vocab_coverage.up.sql
--
-- Substrate types backing VocabCoveragePass — the safetensors decomposer
-- pass that resolves each model token (bpe_token entity from
-- TokenizerMappingPass) against substrate lexical entities (lemma entities
-- seeded by UD / WordNet / Wiktionary) and records cross-source
-- corroboration plus per-architecture coverage statistics.
--
-- Per docs/specs/decomposers/analysis-passes.md § "VocabCoveragePass"
-- (lines 211-217). Depends on TokenizerMappingPass (migration 0045).
--
-- New entity type:
--   vocab_coverage_profile — one entity per (model_architecture, coverage
--                            statistics). Identical coverage statistics
--                            across snapshots collapse to ONE entity.
--                            Modality 'model_weights', shares
--                            entity_default with the rest of the
--                            model-derived analysis entities.
--
-- New edge types:
--   covers_lemma         — bpe_token → lemma. Many edges per bpe_token if
--                          its decoded surface form matches multiple lemma
--                          variants; many edges per lemma across
--                          tokenizers / model snapshots. The substrate's
--                          accumulating evidence that a token covers an
--                          existing seed-lexicon lemma.
--   has_vocab_coverage   — model_architecture → vocab_coverage_profile.
--                          One edge per architecture.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('vocab_coverage_profile', 'model_weights');

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('covers_lemma',       'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'bpe_token'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma')),
    ('has_vocab_coverage', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'vocab_coverage_profile'));
