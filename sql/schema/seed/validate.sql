-- Halt apply if seed counts deviate from canonical inventory.
DO $$
DECLARE
    cnt INT;
BEGIN
    SELECT COUNT(*) INTO cnt FROM substrate.entity_type;
    IF cnt <> 42 THEN RAISE EXCEPTION 'entity_type count=% (expected 42)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.physicality_type;
    IF cnt <> 13 THEN RAISE EXCEPTION 'physicality_type count=% (expected 13)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.edge_role;
    IF cnt <> 7 THEN RAISE EXCEPTION 'edge_role count=% (expected 7)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.significance_context;
    IF cnt <> 10 THEN RAISE EXCEPTION 'significance_context count=% (expected 10)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.provenance;
    IF cnt <> 10 THEN RAISE EXCEPTION 'provenance count=% (expected 10)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.lexname;
    IF cnt <> 45 THEN RAISE EXCEPTION 'lexname count=% (expected 45)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.pos;
    IF cnt <> 17 THEN RAISE EXCEPTION 'pos count=% (expected 17)', cnt; END IF;

    SELECT COUNT(*) INTO cnt FROM substrate.edge_type;
    IF cnt <> 98 THEN RAISE EXCEPTION 'edge_type count=% (expected 98)', cnt; END IF;
END$$;
