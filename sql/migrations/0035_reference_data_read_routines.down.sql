-- 0035_reference_data_read_routines.down.sql

DROP FUNCTION IF EXISTS substrate.reference_id_by_code(text, text);
DROP FUNCTION IF EXISTS substrate.reference_int64_set(text, text);
DROP FUNCTION IF EXISTS substrate.reference_code_text_map(text, text);
DROP FUNCTION IF EXISTS substrate.reference_key_value_map(text, text, text);
DROP FUNCTION IF EXISTS substrate.reference_code_map(text);
