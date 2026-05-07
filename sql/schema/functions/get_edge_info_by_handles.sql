DROP FUNCTION IF EXISTS substrate.get_edge_info_by_handles(INT[], BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_edge_info_by_handles(
        p_edge_type_codes TEXT[], p_hashes BYTEA[]
) RETURNS TABLE (
        edge_type_code TEXT,
        edge_hash BYTEA,
        source_type_code TEXT,
        source_hash BYTEA,
        target_type_code TEXT,
        target_hash BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
        SELECT
                et.code,
                e.hash,
                COALESCE(src_decl.code, src_cls.code),
                src.entity_hash,
                COALESCE(tgt_decl.code, tgt_cls.code),
                tgt.entity_hash
            FROM unnest(p_edge_type_codes, p_hashes) AS requested(type_code, h)
            JOIN substrate.edge_type et ON et.code = requested.type_code
            JOIN substrate.edge e ON e.edge_type_id = et.id AND e.hash = requested.h
            LEFT JOIN substrate.entity_type src_decl ON src_decl.id = et.source_type_id
            LEFT JOIN substrate.entity_type tgt_decl ON tgt_decl.id = et.target_type_id
            LEFT JOIN LATERAL (
                    SELECT em.entity_hash
                        FROM substrate.edge_member em
                        JOIN substrate.edge_role er ON er.id = em.edge_role_id
                     WHERE em.edge_type_id = e.edge_type_id
                         AND em.edge_hash = e.hash
                         AND er.code = 'source'
                     ORDER BY em.role_position, em.entity_hash
                     LIMIT 1
            ) src ON true
            LEFT JOIN LATERAL (
                    SELECT em.entity_hash
                        FROM substrate.edge_member em
                        JOIN substrate.edge_role er ON er.id = em.edge_role_id
                     WHERE em.edge_type_id = e.edge_type_id
                         AND em.edge_hash = e.hash
                         AND er.code = 'target'
                     ORDER BY em.role_position, em.entity_hash
                     LIMIT 1
            ) tgt ON true
            LEFT JOIN LATERAL (
                    SELECT child_et.code
                        FROM substrate.entity_classification ec
                        JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
                     WHERE ec.entity_hash = src.entity_hash
                     ORDER BY child_et.code
                     LIMIT 1
            ) src_cls ON true
            LEFT JOIN LATERAL (
                    SELECT child_et.code
                        FROM substrate.entity_classification ec
                        JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
                     WHERE ec.entity_hash = tgt.entity_hash
                     ORDER BY child_et.code
                     LIMIT 1
            ) tgt_cls ON true;
$f$;
