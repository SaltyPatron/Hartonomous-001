-- Entity types 10, 11: collation_element, language_name.
-- UCD/UCA-side artifacts that aren't codepoints themselves.
CREATE TABLE substrate.entity_unicode
    PARTITION OF substrate.entity FOR VALUES IN (10, 11);
