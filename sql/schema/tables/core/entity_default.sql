-- Catch-all for entity_type_id values not covered by named partitions.
-- New entity types added later via reference seed should ADD a partition
-- before they're used; the default catches accidental drift.
CREATE TABLE substrate.entity_default
    PARTITION OF substrate.entity DEFAULT;
