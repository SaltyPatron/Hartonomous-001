-- 0032_reference_data_routines.down.sql

DROP FUNCTION IF EXISTS substrate.write_codepoint_properties_json(jsonb);
DROP FUNCTION IF EXISTS substrate.populate_break_properties(text[], text[]);
DROP FUNCTION IF EXISTS substrate.populate_blocks(text[], int[], int[]);
DROP FUNCTION IF EXISTS substrate.populate_scripts(text[]);
DROP FUNCTION IF EXISTS substrate.populate_general_categories(text[], text[], text[]);
DROP FUNCTION IF EXISTS substrate.populate_senses(varchar[], text[], int[], int[]);
DROP FUNCTION IF EXISTS substrate.upsert_homogeneous_edge_types(varchar[], varchar, text);
DROP FUNCTION IF EXISTS substrate.update_language_name_entity_ids(char(3)[], bigint[]);
DROP FUNCTION IF EXISTS substrate.populate_languages(char(3)[], varchar[], char(1)[], char(1)[], char(2)[], char(3)[], char(3)[]);
DROP FUNCTION IF EXISTS substrate.populate_deprels(varchar[]);
DROP FUNCTION IF EXISTS substrate.populate_morph_features(varchar[], varchar[]);
DROP FUNCTION IF EXISTS substrate.upsert_reference_edge_type(text, text, text, text);
