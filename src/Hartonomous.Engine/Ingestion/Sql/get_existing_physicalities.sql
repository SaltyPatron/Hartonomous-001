SELECT pt.code AS physicality_type_code, ph.entity_hash, ph.content_hash
  FROM substrate.physicality ph
  JOIN substrate.physicality_type pt ON pt.id = ph.physicality_type_id
  JOIN unnest($1::int[], $2::bytea[], $3::bytea[]) AS probe(pt_id, eh, ch)
    ON ph.physicality_type_id = probe.pt_id
   AND ph.entity_hash         = probe.eh
   AND ph.content_hash        = probe.ch
