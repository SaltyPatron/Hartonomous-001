-- Reverse-lookup index on substrate.provenance_modality: given a modality_code,
-- which provenance sources are authoritative? The composite PK
-- (provenance_id, modality_code) already serves forward lookup; this gives the
-- inverse without scanning the junction.
CREATE INDEX provenance_modality_modality_idx
    ON substrate.provenance_modality (modality_code);
