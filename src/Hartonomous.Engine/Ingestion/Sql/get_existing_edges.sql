SELECT et.code AS edge_type_code, e.hash
  FROM substrate.edge e
  JOIN substrate.edge_type et ON et.id = e.edge_type_id
  JOIN unnest($1::int[], $2::bytea[]) AS probe(et_id, h)
    ON e.edge_type_id = probe.et_id
   AND e.hash         = probe.h
