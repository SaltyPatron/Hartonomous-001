-- Physicality types: exactly 3 rows.
--
-- Per rule 25-physicality-4d, the substrate has three physicality roles
-- and only three. Geometry SHAPE (POINT vs LINESTRING vs MULTILINESTRING
-- vs POLYGON, all ZM) carries the within-role structural distinction
-- the old per-modality codes (s3_position, waveform, contour, etc.) were
-- redundantly encoding. Modality lives on the entity_type of the entity
-- the physicality attaches to, NOT on physicality_type.
--
--   entity  (id 1) — the building block's own structure.
--                    atoms = POINTZM with real content-derived coords
--                            (codepoint Super-Fibonacci S^3 by UCA rank,
--                             audio sample value, pixel intensity, tensor
--                             cell, etc.).
--                    compositions = LINESTRINGZM through child centroids
--                            (word_form = LINESTRING through codepoint
--                             POINTZMs; grapheme_cluster, lemma, morpheme
--                             all live here). MULTILINESTRINGZM for
--                             branching shapes.
--
--   firefly (id 2) — per-model embedding-row POINTZM specimens attached
--                    to existing word_form entities. MULTIPOINTZM aggregate
--                    per entity across ingested models for cross-model
--                    Voronoi consensus.
--
--   content (id 3) — content-tier composition's mantissa-packed
--                    LINESTRINGZM whose vertices ARE child entity hash
--                    refs via substrate.bb_pack_*. text_composition,
--                    paragraph, document, audio_chunk, pixel_region,
--                    video_frame all carry this. The geometry IS the
--                    indexed child manifest. Reverse-resolve via
--                    substrate.entity_by_hash_prefix composite-btree.
INSERT INTO substrate.physicality_type (code) VALUES
    ('entity'),
    ('firefly'),
    ('content');
