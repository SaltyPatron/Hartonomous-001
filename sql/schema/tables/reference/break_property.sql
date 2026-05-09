CREATE TABLE substrate.break_property (
    id       SERIAL PRIMARY KEY,
    code     VARCHAR(32) NOT NULL,
    category VARCHAR(16) NOT NULL,
    enum_id  INT NOT NULL,
    UNIQUE(code, category),
    UNIQUE(category, enum_id)
);

COMMENT ON TABLE substrate.break_property IS
    'UAX #29 break properties for segmentation. Five categories: GCB (grapheme), WB (word), SB (sentence), LB (line), InCB (Indic conjunct break). enum_id is the per-category enum value from the embedded UCD blob (UC_GCB_*, UC_WB_*, UC_SB_*, UC_LB_*, UC_INCB_* in pg_ucd_segmentation.h). codepoint_property FK lookups use (category, enum_id) — robust against ID-offset drift when UCD versions add or reorder enum values.';
