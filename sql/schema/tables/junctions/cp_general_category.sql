CREATE TABLE substrate.cp_general_category (
    entity_hash         substrate.hash_value NOT NULL,
    general_category_id INT NOT NULL REFERENCES substrate.general_category(id),
    PRIMARY KEY (entity_hash, general_category_id)
);

COMMENT ON TABLE substrate.cp_general_category IS
    'Codepoint → UAX #44 General_Category narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_general_category typed edge on substrate.edge; this junction is the index-locality denormalization for fast "all codepoints of GC X" queries.';
