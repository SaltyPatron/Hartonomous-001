-- Blob-driven UCD codepoint atom seed. Uses the embedded UCD blob baked
-- into the hartonomous extension at build time — substrate.cp_hash,
-- substrate.cp_centroid, substrate.cp_{general_category,script,block,
-- bidi_class,east_asian_width,gcb,wb,sb,lb,ccc,extended_pictographic,
-- simple_uppercase/lowercase/titlecase/case_fold} are all single-cp
-- C lookups against the blob. Everything is in-process inside the PG
-- backend; no XML parsing, no Python, no SPI round trips.
--
-- All 1,114,112 codepoints (0..0x10FFFF) get entity + classification +
-- physicality (POINTZM from the blob's pre-computed centroid) + every
-- UCD property junction the blob carries. Single transaction.
--
-- Expected runtime: tens of seconds on 14900KS-class host. The math is
-- already pre-computed at extension build; this just COPY-equivalents
-- INSERT-SELECT it into substrate.* tables.

BEGIN;

-- Resolve reference ids ONCE (AP-23: never inline in row-set SELECTs).
DO $$
DECLARE
    v_codepoint_type_id        INT := (SELECT id FROM substrate.entity_type WHERE code='codepoint');
    v_unicode_prov_id          INT := (SELECT id FROM substrate.provenance WHERE code='unicode_consortium');
    v_entity_physicality_id    INT := (SELECT id FROM substrate.physicality_type WHERE code='entity');
BEGIN
    -- Working set: integer 0..0x10FFFF excluding surrogate range (D800..DFFF
    -- have no scalar value; their codepoint hashes still exist for
    -- content-addressed completeness, but they get no UCD properties since
    -- they're not valid scalars).

    -- 1. Entity rows. Hash from blob. ON CONFLICT covers re-seed.
    INSERT INTO substrate.entity (hash)
    SELECT substrate.cp_hash(cp)::substrate.hash_value
      FROM generate_series(0, 1114111) AS cp
     WHERE substrate.cp_hash(cp) IS NOT NULL
    ON CONFLICT (hash) DO NOTHING;

    -- 2. Entity classification (codepoint × unicode_consortium).
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, v_codepoint_type_id, v_unicode_prov_id
      FROM generate_series(0, 1114111) AS cp
     WHERE substrate.cp_hash(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    -- 3. Per-codepoint POINTZM physicality. Every codepoint has a centroid
    --    from the blob (the blob's pre-gen handles ranked + unranked
    --    deterministically). NOT just the 38K UCA-ranked ones.
    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom, partition_bucket)
    SELECT v_entity_physicality_id,
           substrate.cp_hash(cp)::substrate.hash_value,
           substrate.cp_hash(cp)::substrate.hash_value,
           substrate.geometry4d_to_geometryzm(
               public.cast_point4d_to_geometry4d(substrate.cp_centroid(cp))
           ),
           (get_byte(substrate.cp_hash(cp)::bytea, 0) & 7)::SMALLINT
      FROM generate_series(0, 1114111) AS cp
     WHERE substrate.cp_hash(cp) IS NOT NULL
       AND substrate.cp_centroid(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    -- 4. UCD property junctions — one bulk INSERT per junction table.
    --    Each substrate.cp_<property>(cp) returns the reference-id (int)
    --    or NULL if unset/inapplicable. The blob handles all 1.1M cps.

    -- Blob returns native byte/short index 0-based; substrate ids are
    -- 1-based per seed file convention ("id = native-blob byte code + 1").
    INSERT INTO substrate.cp_general_category (entity_hash, general_category_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, substrate.cp_general_category(cp) + 1
      FROM generate_series(0, 1114111) AS cp
     WHERE substrate.cp_general_category(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    INSERT INTO substrate.cp_script (entity_hash, script_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, substrate.cp_script(cp) + 1
      FROM generate_series(0, 1114111) AS cp
     WHERE substrate.cp_script(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    INSERT INTO substrate.cp_block (entity_hash, block_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, substrate.cp_block(cp) + 1
      FROM generate_series(0, 1114111) AS cp
     WHERE substrate.cp_block(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    INSERT INTO substrate.cp_bidi_class (entity_hash, bidi_class_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, substrate.cp_bidi(cp) + 1
      FROM generate_series(0, 1114111) AS cp
     WHERE substrate.cp_bidi(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    INSERT INTO substrate.cp_east_asian_width (entity_hash, east_asian_width_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, substrate.cp_eaw(cp) + 1
      FROM generate_series(0, 1114111) AS cp
     WHERE substrate.cp_eaw(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    -- break_property uses (category, enum_id) lookup since substrate.id
    -- is a flat sequence over all 5 categories (GCB/WB/SB/LB/INCB).
    INSERT INTO substrate.cp_grapheme_break (entity_hash, break_property_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, bp.id
      FROM generate_series(0, 1114111) AS cp
      JOIN substrate.break_property bp
        ON bp.category = 'GCB' AND bp.enum_id = substrate.cp_gcb(cp)
     WHERE substrate.cp_gcb(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    INSERT INTO substrate.cp_word_break (entity_hash, break_property_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, bp.id
      FROM generate_series(0, 1114111) AS cp
      JOIN substrate.break_property bp
        ON bp.category = 'WB' AND bp.enum_id = substrate.cp_wb(cp)
     WHERE substrate.cp_wb(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    INSERT INTO substrate.cp_sentence_break (entity_hash, break_property_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, bp.id
      FROM generate_series(0, 1114111) AS cp
      JOIN substrate.break_property bp
        ON bp.category = 'SB' AND bp.enum_id = substrate.cp_sb(cp)
     WHERE substrate.cp_sb(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    INSERT INTO substrate.cp_line_break (entity_hash, break_property_id)
    SELECT substrate.cp_hash(cp)::substrate.hash_value, bp.id
      FROM generate_series(0, 1114111) AS cp
      JOIN substrate.break_property bp
        ON bp.category = 'LB' AND bp.enum_id = substrate.cp_lb(cp)
     WHERE substrate.cp_lb(cp) IS NOT NULL
    ON CONFLICT DO NOTHING;

    RAISE NOTICE 'UCD blob-driven seed complete: % codepoints',
        (SELECT count(*) FROM substrate.entity_classification ec
          JOIN substrate.entity_type et ON et.id=ec.entity_type_id
         WHERE et.code='codepoint');
END $$;

COMMIT;
