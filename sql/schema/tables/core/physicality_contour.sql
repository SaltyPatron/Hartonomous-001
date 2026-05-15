-- Physicality type 13: contour. LINESTRINGZM whose vertices ARE the real
-- centroids of the composition's children in canonical role / sequence
-- order — NO mantissa packing of identity bits (per memory
-- `feedback-no-mantissa-vertex-packing` and the S3.D chunk-1 corrected
-- model). Universal carrier for COMPOSITION entities at every tier:
--   * Entity-tier (word_form, lemma, morpheme): vertices are the real
--     codepoint POINTZMs — for "cat" (word_form), three vertices =
--     c.centroid, a.centroid, t.centroid each read from physicality_s3.
--   * Content-tier (text_composition, sentence, paragraph, document,
--     audio_chunk, image_region, video_shot): vertices are the real
--     centroids of the constituent entities — for "the cat sat on the
--     mat" (text_composition), six vertices = the.centroid, cat.centroid,
--     sat.centroid, on.centroid, the.centroid, mat.centroid.
--
-- Multi-segment / branching / parallel-sub-sequence compositions use
-- MULTILINESTRINGZM. Identity of which children are referenced lives in
-- the relational child-tracking layer; geometry stores SHAPE only.
-- ST_Frechet / Hausdorff over two contour geoms compares trajectory shape
-- in real-coord space (analogy completion, frayed-edge detection,
-- application-fault matching across structurally-similar trajectories
-- whose categorical labels differ).
--
-- Split landed: physicality_entity_shape (id 15) carries real-coord canonical
-- structural fingerprints for Fréchet shape matching; physicality_ingestion_trajectory
-- (id 16) carries mantissa-packed identity-level child manifests for O(tier)
-- reconstruction via substrate.entity_by_hash_prefix. This contour partition is
-- LEGACY — retained while existing decomposers still emit
-- AddPhysicalityLineString4d(parent, "contour", verts) — and is on a deprecation
-- path. New decomposers route to AddEntityShape (entity_shape partition) for
-- real-coord canonical shape, or AddIngestionTrajectory (ingestion_trajectory
-- partition) for the mantissa-packed structural child manifest, depending on
-- which substrate surface they're contributing to.
CREATE TABLE substrate.physicality_contour
    PARTITION OF substrate.physicality FOR VALUES IN (13);
ALTER TABLE substrate.physicality_contour
    ADD CONSTRAINT physicality_contour_linestringzm
    CHECK (GeometryType(geom) IN ('LINESTRING', 'MULTILINESTRING')
           AND ST_NDims(geom) = 4);
