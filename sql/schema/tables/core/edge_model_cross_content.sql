-- Partition for cross-content attestation edge_types (IDs 63..65 per
-- sql/schema/seed/edge_type.sql):
--   63 model_spatial_pattern    (pixel_region↔pixel_region or audio_chunk↔audio_chunk)
--   64 model_cross_modal_pattern (text↔image, text↔audio, decoder-token↔encoder-token)
--   65 model_detection_class     (object_query↔visual_concept)
--
-- High-cardinality when vision / audio / detection models are ingested.
-- Co-located in one partition because the three share the cross-modality
-- access pattern (recompose for vision tower / cross-encoder / detection
-- head reads attestations across all three edge_types together).
CREATE TABLE substrate.edge_model_cross_content
    PARTITION OF substrate.edge FOR VALUES IN (63, 64, 65);
