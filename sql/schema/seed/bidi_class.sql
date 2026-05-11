INSERT INTO substrate.bidi_class (id, code, description) VALUES
    (1,  'L',   'Left_To_Right'),
    (2,  'R',   'Right_To_Left'),
    (3,  'AL',  'Arabic_Letter'),
    (4,  'EN',  'European_Number'),
    (5,  'ES',  'European_Separator'),
    (6,  'ET',  'European_Terminator'),
    (7,  'AN',  'Arabic_Number'),
    (8,  'CS',  'Common_Separator'),
    (9,  'NSM', 'Nonspacing_Mark'),
    (10, 'BN',  'Boundary_Neutral'),
    (11, 'B',   'Paragraph_Separator'),
    (12, 'S',   'Segment_Separator'),
    (13, 'WS',  'White_Space'),
    (14, 'ON',  'Other_Neutral'),
    (15, 'LRE', 'Left_To_Right_Embedding'),
    (16, 'LRO', 'Left_To_Right_Override'),
    (17, 'RLE', 'Right_To_Left_Embedding'),
    (18, 'RLO', 'Right_To_Left_Override'),
    (19, 'PDF', 'Pop_Directional_Format'),
    (20, 'LRI', 'Left_To_Right_Isolate'),
    (21, 'RLI', 'Right_To_Left_Isolate'),
    (22, 'FSI', 'First_Strong_Isolate'),
    (23, 'PDI', 'Pop_Directional_Isolate')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    description = EXCLUDED.description;

SELECT setval('substrate.bidi_class_id_seq', (SELECT max(id) FROM substrate.bidi_class));
