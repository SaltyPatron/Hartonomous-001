SELECT et.code AS edge_type_code,
       em.edge_hash,
       em.entity_hash,
       er.code AS role_code,
       em.role_position
  FROM substrate.edge_member em
  JOIN substrate.edge_type et ON et.id = em.edge_type_id
  JOIN substrate.edge_role er ON er.id = em.edge_role_id
  JOIN unnest($1::int[], $2::bytea[], $3::bytea[], $4::int[], $5::int[])
       AS probe(et_id, eh, entity_h, role_id, pos)
    ON em.edge_type_id  = probe.et_id
   AND em.edge_hash     = probe.eh
   AND em.entity_hash   = probe.entity_h
   AND em.edge_role_id  = probe.role_id
   AND em.role_position = probe.pos
