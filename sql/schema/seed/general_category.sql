-- GENERATED — Unicode General_Category property (UAX #44).
-- Source: ext/hartonomous_pg/src/generated/pg_ucd_inventory.c (uc_inv_gc).
-- id = native-blob byte code + 1; matches substrate.codepoint_property FK convention.
INSERT INTO substrate.general_category (id, code, group_code, description) VALUES
    (1, 'Cn', 'C', 'Unassigned'),
    (2, 'Lu', 'L', 'Uppercase_Letter'),
    (3, 'Ll', 'L', 'Lowercase_Letter'),
    (4, 'Lt', 'L', 'Titlecase_Letter'),
    (5, 'Lm', 'L', 'Modifier_Letter'),
    (6, 'Lo', 'L', 'Other_Letter'),
    (7, 'Mn', 'M', 'Nonspacing_Mark'),
    (8, 'Mc', 'M', 'Spacing_Mark'),
    (9, 'Me', 'M', 'Enclosing_Mark'),
    (10, 'Nd', 'N', 'Decimal_Number'),
    (11, 'Nl', 'N', 'Letter_Number'),
    (12, 'No', 'N', 'Other_Number'),
    (13, 'Pc', 'P', 'Connector_Punctuation'),
    (14, 'Pd', 'P', 'Dash_Punctuation'),
    (15, 'Ps', 'P', 'Open_Punctuation'),
    (16, 'Pe', 'P', 'Close_Punctuation'),
    (17, 'Pi', 'P', 'Initial_Punctuation'),
    (18, 'Pf', 'P', 'Final_Punctuation'),
    (19, 'Po', 'P', 'Other_Punctuation'),
    (20, 'Sm', 'S', 'Math_Symbol'),
    (21, 'Sc', 'S', 'Currency_Symbol'),
    (22, 'Sk', 'S', 'Modifier_Symbol'),
    (23, 'So', 'S', 'Other_Symbol'),
    (24, 'Zs', 'Z', 'Space_Separator'),
    (25, 'Zl', 'Z', 'Line_Separator'),
    (26, 'Zp', 'Z', 'Paragraph_Separator'),
    (27, 'Cc', 'C', 'Control'),
    (28, 'Cf', 'C', 'Format'),
    (29, 'Cs', 'C', 'Surrogate'),
    (30, 'Co', 'C', 'Private_Use')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    group_code = EXCLUDED.group_code,
    description = EXCLUDED.description;

SELECT setval('substrate.general_category_id_seq', (SELECT max(id) FROM substrate.general_category));
