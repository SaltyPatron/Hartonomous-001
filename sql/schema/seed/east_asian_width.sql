INSERT INTO substrate.east_asian_width (id, code, description) VALUES
    (1, 'N',  'Neutral'),
    (2, 'Na', 'Narrow'),
    (3, 'A',  'Ambiguous'),
    (4, 'W',  'Wide'),
    (5, 'F',  'Fullwidth'),
    (6, 'H',  'Halfwidth')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    description = EXCLUDED.description;

SELECT setval('substrate.east_asian_width_id_seq', (SELECT max(id) FROM substrate.east_asian_width));
