CREATE TABLE substrate.morph_feature (
    id        SERIAL PRIMARY KEY,
    key       VARCHAR(32) NOT NULL,
    value     VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.morph_feature(id),
    UNIQUE(key, value)
);

COMMENT ON TABLE substrate.morph_feature IS
    'Morphological feature key-value pairs (Number=Sing, Tense=Past, Mood=Ind, etc.). Each row = one (key, value).';
COMMENT ON COLUMN substrate.morph_feature.parent_id IS
    'Groups values under a common feature key row.';
