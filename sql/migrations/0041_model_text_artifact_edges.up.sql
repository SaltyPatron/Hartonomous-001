-- 0041_model_text_artifact_edges.up.sql
--
-- Adds the structural edge types that link a `model_architecture` entity to
-- its on-disk text artifacts after they are decomposed into the substrate as
-- `text_composition` entities.
--
-- Without these edges, config.json / tokenizer.json / merges.txt /
-- special_tokens_map.json / chat_template.jinja / generation_config.json /
-- README.md never enter the substrate at all — the safetensors decomposer
-- only ingests tensor weights, leaving the model package literally
-- un-recomposable: there's no tokenizer to tokenize prompts with, no config
-- to instantiate a target architecture from, no chat template to format
-- conversations with. The recomposer needs all of these to emit a loadable
-- safetensors package.
--
-- The artifacts themselves are content-addressed via the text decomposer's
-- Merkle DAG (codepoint → grapheme → word → sentence → text_composition),
-- so the SAME tokenizer.json shipped across two model snapshots collapses
-- to ONE substrate text_composition with two `has_tokenizer_artifact`
-- edges — one per model — automatically.
--
-- Category = 'structural'. New edges with assigned ids > 33 route into
-- substrate.edge_default per the partition layout in 0006 (precedent: 0037).

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_config_artifact',              'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_tokenizer_artifact',           'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_tokenizer_config_artifact',    'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_special_tokens_artifact',      'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_merges_artifact',              'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_chat_template_artifact',       'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_generation_config_artifact',   'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_readme_artifact',              'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition'));
