-- substrate.entity is content-addressed: same BLAKE3 hash → same row.
-- Identity ONLY. No id, no entity_type_id, no centroid columns, no hilbert
-- index, no stored hash-prefix columns, no partition_bucket column.
-- Classifications go on substrate.entity_classification. Geometry lives on
-- substrate.physicality.
--
-- substrate.entity does NOT hold atom rows. Atom-tier content for
-- Unicode/ISO modalities is owned by the pre-gen blob — codepoints are
-- enumerated in 0..0x10FFFF with S³ coords from UCA-sequenced Super-
-- Fibonacci, accessed via in-process microsecond functions
-- (hartonomous_ucd_cp_centroid, cp_hash, cp_from_hash, cp_from_centroid,
-- cp_gc / cp_script / cp_block / cp_bidi / cp_eaw / cp_*break, etc.).
-- The substrate's first concrete tier is BUILDING-BLOCK COMPOSITION
-- (grapheme_cluster, word_form, lemma, morpheme, ...) — those carry real
-- geometry through codepoint POINTZMs as entity-tier LINESTRINGZM vertices
-- (real coords from blob, NOT mantissa-packed). Content trajectories
-- (text_composition, paragraph, document, audio_recording, etc.) live
-- here too; their physicality is content-tier mantissa-packed
-- LINESTRINGZM through building-block entity refs.
--
-- LIST-partitioned by (get_byte(hash, 0) & 7) — 8 children, bottom 3 bits
-- of the hash's lowest byte, which is identical to the bottom 3 bits of
-- the mantissa-pack X coordinate's hash_bits_0_51 slice. This alignment
-- means content-tier vertex reverse-resolve partition-prunes at the
-- planner: given a vertex's unpacked hash prefix, the planner knows which
-- child partition holds the referenced entity. C# routes writes to the
-- same partition by computing `(int)(hash[0] & 7)` — deterministic and
-- matches PG's expression evaluation.
--
-- PARTITION BY LIST on an expression is allowed in PG18; UNIQUE / PRIMARY
-- KEY constraints on the table must include all columns referenced by the
-- partition expression — `hash` is the only column referenced, and the PK
-- is on `hash`, so the constraint holds without a synthetic partition_
-- bucket column.
--
-- Content-tier vertex reverse-resolve from (X, Z) mantissa-packed hash
-- prefixes back to full entity hashes uses entity_hash_prefix_idx — a
-- functional btree on (substrate.bb_hash_lo(hash), substrate.bb_hash_hi(
-- hash)). No stored generated columns required.
CREATE TABLE substrate.entity (
    hash substrate.hash_value PRIMARY KEY
) PARTITION BY LIST ((get_byte(hash, 0) & 7));

CREATE TABLE substrate.entity_p0 PARTITION OF substrate.entity FOR VALUES IN (0);
CREATE TABLE substrate.entity_p1 PARTITION OF substrate.entity FOR VALUES IN (1);
CREATE TABLE substrate.entity_p2 PARTITION OF substrate.entity FOR VALUES IN (2);
CREATE TABLE substrate.entity_p3 PARTITION OF substrate.entity FOR VALUES IN (3);
CREATE TABLE substrate.entity_p4 PARTITION OF substrate.entity FOR VALUES IN (4);
CREATE TABLE substrate.entity_p5 PARTITION OF substrate.entity FOR VALUES IN (5);
CREATE TABLE substrate.entity_p6 PARTITION OF substrate.entity FOR VALUES IN (6);
CREATE TABLE substrate.entity_p7 PARTITION OF substrate.entity FOR VALUES IN (7);

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes — building-block compositions and content trajectories only; atom-tier content for Unicode/ISO is owned by the pre-gen blob and is not stored here. Identity = BLAKE3 hash of content; hash is the PK and only column. Classifications live on substrate.entity_classification. LIST-partitioned by (get_byte(hash, 0) & 7) over 8 children entity_p0..entity_p7 — bottom 3 bits of the mantissa-pack X coordinate, so content-tier vertex reverse-resolve partition-prunes at the planner. Geometry lives on substrate.physicality, partitioned by physicality_type_id; entity-tier physicality (building-block compositions) carries real-coord LINESTRINGZMs through atom POINTZMs sourced from blob accessors; content-tier physicality carries mantissa-packed LINESTRINGZMs through entity refs.';
