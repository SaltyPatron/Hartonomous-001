-- 0041_model_text_artifact_edges.down.sql

DELETE FROM substrate.edge_type
 WHERE code IN (
    'has_config_artifact',
    'has_tokenizer_artifact',
    'has_tokenizer_config_artifact',
    'has_special_tokens_artifact',
    'has_merges_artifact',
    'has_chat_template_artifact',
    'has_generation_config_artifact',
    'has_readme_artifact'
 );
