-- Junction: which modalities a provenance source is authoritative in.
-- Replaces the prior substrate.provenance.modality_codes array column —
-- proper relational shape with composite PK and bidirectional btree
-- indexes (no array column, no 1NF violation, no FK-integrity bypass).
CREATE TABLE substrate.provenance_modality (
    provenance_id INT NOT NULL REFERENCES substrate.provenance(id) ON DELETE CASCADE,
    modality_code substrate.modality_code NOT NULL,
    PRIMARY KEY (provenance_id, modality_code)
);

CREATE INDEX provenance_modality_modality_idx
    ON substrate.provenance_modality (modality_code);

COMMENT ON TABLE substrate.provenance_modality IS
    'Junction table: which modalities a provenance source is authoritative in. Replaces the prior modality_codes array column on substrate.provenance — proper relational shape (atomic columns, composite PK, FK to substrate.provenance(id), bidirectional indexes). Empty join = source authoritative for none / text default.';
