-- Codepoint properties indexed by entity hash. Phase C unification:
-- hash-only entity reference (substrate.entity has hash-only PK).
CREATE TABLE substrate.codepoint_property (
    entity_hash              substrate.hash_value PRIMARY KEY REFERENCES substrate.entity(hash),
    codepoint_value          INT  NOT NULL,
    general_category_id      INT  NOT NULL REFERENCES substrate.general_category(id),
    script_id                INT  NOT NULL REFERENCES substrate.script(id),
    block_id                 INT  NOT NULL REFERENCES substrate.block(id),
    bidi_class_id            INT  NOT NULL REFERENCES substrate.bidi_class(id),
    east_asian_width_id      INT  NOT NULL REFERENCES substrate.east_asian_width(id),
    gcb_id                   INT  REFERENCES substrate.break_property(id),
    wb_id                    INT  REFERENCES substrate.break_property(id),
    sb_id                    INT  REFERENCES substrate.break_property(id),
    lb_id                    INT  REFERENCES substrate.break_property(id),
    uca_index                INT  NOT NULL DEFAULT 0,
    hangul_syllable_type     SMALLINT NOT NULL DEFAULT 0,
    numeric_type             SMALLINT NOT NULL DEFAULT 0,
    is_extended_pictographic BOOLEAN NOT NULL DEFAULT FALSE,
    ccc                      SMALLINT NOT NULL DEFAULT 0,
    name                     TEXT,
    decomposition_type       VARCHAR(16),
    decomposition_mapping    INT[],
    simple_uppercase         INT,
    simple_lowercase         INT,
    simple_titlecase         INT,
    simple_case_fold         INT,
    full_case_fold           INT[]
);

COMMENT ON TABLE substrate.codepoint_property IS
    'Codepoint → Unicode properties. Hash-only entity reference with parent substrate.entity FK.';
