CREATE TABLE substrate.codepoint_property (
    entity_type_id           INT  NOT NULL,
    entity_hash              substrate.hash_value NOT NULL,
    codepoint_value          INT  NOT NULL,
    general_category_id      INT  NOT NULL REFERENCES substrate.general_category(id),
    script_id                INT  NOT NULL REFERENCES substrate.script(id),
    block_id                 INT  NOT NULL REFERENCES substrate.block(id),
    gcb_id                   INT  REFERENCES substrate.break_property(id),
    wb_id                    INT  REFERENCES substrate.break_property(id),
    sb_id                    INT  REFERENCES substrate.break_property(id),
    lb_id                    INT  REFERENCES substrate.break_property(id),
    is_extended_pictographic BOOLEAN NOT NULL DEFAULT FALSE,
    ccc                      SMALLINT NOT NULL DEFAULT 0,
    decomposition_type       VARCHAR(16),
    decomposition_mapping    INT[],
    simple_case_fold         INT,
    full_case_fold           INT[],
    PRIMARY KEY (entity_type_id, entity_hash)
    -- FK to substrate.entity application-enforced (PG18.3 partitionwise-FK SEGV).
);
CREATE INDEX idx_codepoint_property_codepoint ON substrate.codepoint_property(codepoint_value);
CREATE INDEX idx_codepoint_property_gc        ON substrate.codepoint_property(general_category_id);
CREATE INDEX idx_codepoint_property_script    ON substrate.codepoint_property(script_id);
CREATE INDEX idx_codepoint_property_block     ON substrate.codepoint_property(block_id);
COMMENT ON TABLE substrate.codepoint_property IS
    'Codepoint → Unicode properties. One row per codepoint entity. Carries the original codepoint integer + UCD-derived classifications + casefolding/decomposition.';
