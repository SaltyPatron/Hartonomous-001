CREATE TABLE substrate.cp_script (
    entity_hash substrate.hash_value NOT NULL,
    script_id   INT NOT NULL REFERENCES substrate.script(id),
    PRIMARY KEY (entity_hash, script_id)
);

COMMENT ON TABLE substrate.cp_script IS
    'Codepoint → UAX #24 / ISO 15924 Script narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_script typed edge.';
