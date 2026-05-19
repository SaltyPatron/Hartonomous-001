CREATE TABLE substrate.cp_block (
    entity_hash substrate.hash_value NOT NULL,
    block_id    INT NOT NULL REFERENCES substrate.block(id),
    PRIMARY KEY (entity_hash, block_id)
);

COMMENT ON TABLE substrate.cp_block IS
    'Codepoint → UAX #44 Block narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_block typed edge.';
