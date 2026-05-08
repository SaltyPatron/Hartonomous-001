SELECT ec.entity_hash, et.code AS entity_type_code, p.code AS provenance_code
  FROM substrate.entity_classification ec
  JOIN substrate.entity_type et ON et.id = ec.entity_type_id
  JOIN substrate.provenance  p  ON p.id  = ec.provenance_id
  JOIN unnest($1::bytea[], $2::int[], $3::int[]) AS probe(h, et_id, p_id)
    ON ec.entity_hash    = probe.h
   AND ec.entity_type_id = probe.et_id
   AND ec.provenance_id  = probe.p_id
