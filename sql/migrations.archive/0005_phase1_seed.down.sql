-- 0005_phase1_seed.down.sql
-- Truncate + restart identity so a subsequent up produces the same SERIAL IDs.
TRUNCATE TABLE
    substrate.edge_type,
    substrate.pos,
    substrate.lexname,
    substrate.provenance,
    substrate.significance_context,
    substrate.edge_role,
    substrate.physicality_type,
    substrate.entity_type
RESTART IDENTITY CASCADE;
