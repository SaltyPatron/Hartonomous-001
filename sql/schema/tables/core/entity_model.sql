-- Entity types 16..42: Track 2 substrate — model decomposition.
-- Per-role unit decomposition + per-tensor analysis surfaces.
-- See sql/schema/seed/entity_type.sql for the full inventory.
CREATE TABLE substrate.entity_model
    PARTITION OF substrate.entity FOR VALUES IN
        (16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
         30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42);
