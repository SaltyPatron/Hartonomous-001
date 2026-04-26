-- 0045_tokenizer_mapping.up.sql
--
-- Substrate types backing TokenizerMappingPass — the safetensors decomposer
-- pass that parses each model's shipped tokenizer.json/vocab.json/SP/tiktoken
-- artifact, emits its vocabulary as content-addressed substrate entities, and
-- links them back to the model_architecture.
--
-- The bridge this pass establishes is what makes ingest → seed-lexicon
-- corroboration possible later (VocabCoveragePass, etc.). Without it the
-- model knows "token id 7234 is at vocab row 7234" but the substrate has no
-- symbolic anchor to attach lexical evidence to.
--
-- Per docs/specs/decomposers/tokenizers.md § "Entity mapping" (table
-- starting line 263):
--
--   | Primitive output            | Entity kind        | Signature fields                       |
--   | TokenizerModel.ConfigHash   | tokenizer_model    | canonicalized config bytes             |
--   | VocabularyEntry             | bpe_token (exists) | (tokenizer_model hash, token bytes)    |
--
-- New entity type:
--   tokenizer_model — one entity per parsed tokenizer config. Identical
--                     tokenizer.json across snapshots (canonicalized) →
--                     ONE entity with multiple has_tokenizer_model edges,
--                     one per model that ships it. Modality 'model_weights'
--                     to share the entity_default partition with the rest of
--                     the model-derived analysis entities seeded in 0042.
--
-- bpe_token already exists (entity_type id 12). TokenizerMappingPass emits
-- these hashed by canonical (tokenizer_config_hash, token_bytes). Note that
-- the existing EmbeddingFireflyPass also emits bpe_token entities, hashed
-- by 4D firefly coordinates (geometric identity); the two coexist in the
-- bpe_token partition with different hashes. The plan's A10 task realigns
-- the firefly pass to attach physicality to symbolic-bpe_token entities
-- created here, eliminating the geometric-identity bpe_token rows.
--
-- New edge types:
--   has_tokenizer_model    — model_architecture → tokenizer_model
--   has_token_in_tokenizer — tokenizer_model    → bpe_token
--                            (one edge per vocabulary entry)
--
-- Per-token codepoint composition uses substrate.sequence (parent=bpe_token,
-- child=codepoint, ordinal=position) — same pattern the text decomposer
-- already uses for grapheme_cluster → codepoint. No new edge type required
-- for that linkage; the parent/child/ordinal data already lives on
-- substrate.sequence.
--
-- Category = 'structural' — both new edges are typed structural relations
-- between substrate entities. Edge ids > 33 route into substrate.edge_default
-- per the partition layout in 0006.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('tokenizer_model', 'model_weights');

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_tokenizer_model',    'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'tokenizer_model')),
    ('has_token_in_tokenizer', 'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'tokenizer_model'),
        (SELECT id FROM substrate.entity_type WHERE code = 'bpe_token'));
