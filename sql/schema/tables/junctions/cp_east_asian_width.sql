CREATE TABLE substrate.cp_east_asian_width (
    entity_hash         substrate.hash_value NOT NULL,
    east_asian_width_id INT NOT NULL REFERENCES substrate.east_asian_width(id),
    PRIMARY KEY (entity_hash, east_asian_width_id)
);

COMMENT ON TABLE substrate.cp_east_asian_width IS
    'Codepoint → UAX #11 East_Asian_Width narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_east_asian_width typed edge.';
