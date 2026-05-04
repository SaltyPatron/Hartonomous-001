-- substrate.tensor_provenance_chain(p_tensor_hash bytea)
--
-- Full provenance walk for a single tensor: which model_architecture(s)
-- contain it, which provenances contributed evidence, with significance per
-- arena. The recomposer's __metadata__.hartonomous_provenance_chain is built
-- by joining this output across every output tensor.
DROP FUNCTION IF EXISTS substrate.tensor_provenance_chain(bytea);
CREATE OR REPLACE FUNCTION substrate.tensor_provenance_chain(p_tensor_hash bytea)
RETURNS TABLE (
    model_arch_hash      bytea,
    edge_type_code       text,
    provenance_code      text,
    arena_code           text,
    mu                   double precision,
    sigma                double precision,
    games                int
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT em_tgt.entity_hash      AS model_arch_hash,
           et.code                 AS edge_type_code,
           prov.code               AS provenance_code,
           sc.code                 AS arena_code,
           es.mu, es.sigma, es.games
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.category = 'model_derived'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge e
        ON e.edge_type_id = em_src.edge_type_id
       AND e.hash         = em_src.edge_hash
      JOIN substrate.provenance prov   ON prov.id = e.provenance_id
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = e.edge_type_id
       AND es.edge_hash    = e.hash
      LEFT JOIN substrate.significance_context sc
        ON sc.id = es.context_type_id
     WHERE em_src.entity_hash = p_tensor_hash
     ORDER BY arena_code NULLS LAST, mu DESC NULLS LAST;
$$;

COMMENT ON FUNCTION substrate.tensor_provenance_chain(bytea) IS
    'Full provenance walk for a tensor: model_architecture(s) it''s in, provenances that contributed, arena μ/σ/games. Used by recomposer __metadata__ audit chain emission.';
