WITH entity_existing AS (
    SELECT 0::int AS kind,
           NULL::text AS code_a,
           NULL::text AS code_b,
           e.hash AS hash_a,
           NULL::bytea AS hash_b,
           NULL::int AS position
      FROM substrate.entity e
     WHERE e.hash = ANY($1::bytea[])
),
classification_existing AS (
    SELECT 1::int AS kind,
           et.code AS code_a,
           p.code AS code_b,
           ec.entity_hash AS hash_a,
           NULL::bytea AS hash_b,
           NULL::int AS position
      FROM substrate.entity_classification ec
      JOIN substrate.entity_type et ON et.id = ec.entity_type_id
      JOIN substrate.provenance  p  ON p.id  = ec.provenance_id
      JOIN unnest($2::bytea[], $3::int[], $4::int[]) AS probe(h, et_id, p_id)
        ON ec.entity_hash    = probe.h
       AND ec.entity_type_id = probe.et_id
       AND ec.provenance_id  = probe.p_id
),
edge_existing AS (
    SELECT 2::int AS kind,
           et.code AS code_a,
           NULL::text AS code_b,
           e.hash AS hash_a,
           NULL::bytea AS hash_b,
           NULL::int AS position
      FROM substrate.edge e
      JOIN substrate.edge_type et ON et.id = e.edge_type_id
      JOIN unnest($5::int[], $6::bytea[]) AS probe(et_id, h)
        ON e.edge_type_id = probe.et_id
       AND e.hash         = probe.h
),
edge_member_existing AS (
    SELECT 3::int AS kind,
           et.code AS code_a,
           er.code AS code_b,
           em.edge_hash AS hash_a,
           em.entity_hash AS hash_b,
           em.role_position AS position
      FROM substrate.edge_member em
      JOIN substrate.edge_type et ON et.id = em.edge_type_id
      JOIN substrate.edge_role er ON er.id = em.edge_role_id
      JOIN unnest($7::int[], $8::bytea[], $9::bytea[], $10::int[], $11::int[])
           AS probe(et_id, eh, entity_h, role_id, pos)
        ON em.edge_type_id  = probe.et_id
       AND em.edge_hash     = probe.eh
       AND em.entity_hash   = probe.entity_h
       AND em.edge_role_id  = probe.role_id
       AND em.role_position = probe.pos
),
physicality_existing AS (
    SELECT 4::int AS kind,
           pt.code AS code_a,
           NULL::text AS code_b,
           ph.entity_hash AS hash_a,
           ph.content_hash AS hash_b,
           NULL::int AS position
      FROM substrate.physicality ph
      JOIN substrate.physicality_type pt ON pt.id = ph.physicality_type_id
      JOIN unnest($12::int[], $13::bytea[], $14::bytea[]) AS probe(pt_id, eh, ch)
        ON ph.physicality_type_id = probe.pt_id
       AND ph.entity_hash         = probe.eh
       AND ph.content_hash        = probe.ch
),
sequence_existing AS (
    SELECT 5::int AS kind,
           NULL::text AS code_a,
           NULL::text AS code_b,
           s.parent_hash AS hash_a,
           NULL::bytea AS hash_b,
           s.ordinal AS position
      FROM substrate.sequence s
      JOIN unnest($15::bytea[], $16::int[]) AS probe(ph, ord)
        ON s.parent_hash = probe.ph
       AND s.ordinal     = probe.ord
),
entity_significance_existing AS (
    SELECT 6::int AS kind,
           sc.code AS code_a,
           at.code AS code_b,
           es.entity_hash AS hash_a,
           NULL::bytea AS hash_b,
           NULL::int AS position
      FROM substrate.entity_significance es
      JOIN substrate.significance_context sc ON sc.id = es.context_type_id
      JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
      JOIN unnest($17::int[], $18::bytea[], $19::int[]) AS probe(ctx_id, eh, at_id)
        ON es.context_type_id     = probe.ctx_id
       AND es.entity_hash         = probe.eh
       AND es.attestation_type_id = probe.at_id
)
SELECT kind, code_a, code_b, hash_a, hash_b, position FROM entity_existing
UNION ALL
SELECT kind, code_a, code_b, hash_a, hash_b, position FROM classification_existing
UNION ALL
SELECT kind, code_a, code_b, hash_a, hash_b, position FROM edge_existing
UNION ALL
SELECT kind, code_a, code_b, hash_a, hash_b, position FROM edge_member_existing
UNION ALL
SELECT kind, code_a, code_b, hash_a, hash_b, position FROM physicality_existing
UNION ALL
SELECT kind, code_a, code_b, hash_a, hash_b, position FROM sequence_existing
UNION ALL
SELECT kind, code_a, code_b, hash_a, hash_b, position FROM entity_significance_existing
