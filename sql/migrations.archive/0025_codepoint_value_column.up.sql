-- 0025 — Add codepoint_value INT to substrate.codepoint_property.
--
-- The codepoint integer value is the input to the BLAKE3 identity hash but is
-- not recoverable from the hash alone. Storing it explicitly avoids an
-- expensive reverse-map computation when loading the segmentation cache.

ALTER TABLE substrate.codepoint_property
    ADD COLUMN codepoint_value INT;

CREATE UNIQUE INDEX idx_codepoint_property_value
    ON substrate.codepoint_property(codepoint_value)
    WHERE codepoint_value IS NOT NULL;

COMMENT ON COLUMN substrate.codepoint_property.codepoint_value IS
    'Unicode scalar value (0..0x10FFFF). Stored explicitly because the BLAKE3 entity hash is not reversible.';
