-- 0045_tokenizer_mapping.down.sql
DELETE FROM substrate.edge_type WHERE code IN ('has_tokenizer_model', 'has_token_in_tokenizer');
DELETE FROM substrate.entity_type WHERE code = 'tokenizer_model';
