CREATE TABLE substrate.break_property (
    id       SERIAL PRIMARY KEY,
    code     VARCHAR(32) NOT NULL,
    category VARCHAR(16) NOT NULL,
    UNIQUE(code, category)
);

COMMENT ON TABLE substrate.break_property IS
    'UAX #29 break properties for segmentation. Four categories: GCB (grapheme), WB (word), SB (sentence), LB (line).';
