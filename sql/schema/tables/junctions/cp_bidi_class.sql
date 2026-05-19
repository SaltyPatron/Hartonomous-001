CREATE TABLE substrate.cp_bidi_class (
    entity_hash   substrate.hash_value NOT NULL,
    bidi_class_id INT NOT NULL REFERENCES substrate.bidi_class(id),
    PRIMARY KEY (entity_hash, bidi_class_id)
);

COMMENT ON TABLE substrate.cp_bidi_class IS
    'Codepoint → UAX #9 Bidi_Class narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_bidi_class typed edge.';
