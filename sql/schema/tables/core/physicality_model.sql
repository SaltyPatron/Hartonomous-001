-- Physicality types 11..12: svd_spectrum, weight_distribution.
-- Both 4D (POINTZM or LINESTRINGZM); enforced per-row.
CREATE TABLE substrate.physicality_model
    PARTITION OF substrate.physicality FOR VALUES IN (11, 12);
