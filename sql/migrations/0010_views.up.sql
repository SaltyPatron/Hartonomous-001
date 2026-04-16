-- 0010_views.up.sql
-- Views per specs/sql/views.md and specs/operations/monitoring.md.

CREATE OR REPLACE VIEW monitor.substrate_dashboard AS
SELECT
    (SELECT COUNT(*) FROM substrate.entity) AS total_entities,
    (SELECT COUNT(*) FROM substrate.edge) AS total_edges,
    (SELECT COUNT(*) FROM substrate.physicality) AS total_physicalities,
    (SELECT COUNT(*) FROM substrate.significance) AS total_significance_records,

    (SELECT jsonb_agg(jsonb_build_object('type', et.code, 'count', cnt))
     FROM (
         SELECT entity_type_id, COUNT(*) AS cnt
         FROM substrate.entity GROUP BY entity_type_id ORDER BY cnt DESC LIMIT 10
     ) sub
     JOIN substrate.entity_type et ON et.id = sub.entity_type_id
    ) AS entities_by_type_top10,

    (SELECT jsonb_agg(jsonb_build_object('type', et.code, 'count', cnt))
     FROM (
         SELECT edge_type_id, COUNT(*) AS cnt
         FROM substrate.edge GROUP BY edge_type_id ORDER BY cnt DESC LIMIT 10
     ) sub
     JOIN substrate.edge_type et ON et.id = sub.edge_type_id
    ) AS edges_by_type_top10,

    (SELECT jsonb_agg(jsonb_build_object(
         'arena', sc.code, 'count', stats.cnt,
         'mean_mu', ROUND(stats.avg_mu::NUMERIC, 2),
         'mean_sigma', ROUND(stats.avg_sigma::NUMERIC, 2),
         'min_mu', ROUND(stats.min_mu::NUMERIC, 2),
         'max_mu', ROUND(stats.max_mu::NUMERIC, 2)
     ))
     FROM (
         SELECT context_type_id, COUNT(*) AS cnt,
                AVG(mu) AS avg_mu, AVG(sigma) AS avg_sigma,
                MIN(mu) AS min_mu, MAX(mu) AS max_mu
         FROM substrate.significance GROUP BY context_type_id
     ) stats
     JOIN substrate.significance_context sc ON sc.id = stats.context_type_id
    ) AS significance_by_arena,

    (SELECT jsonb_agg(jsonb_build_object(
         'table', t.relname,
         'total_size', pg_size_pretty(pg_total_relation_size(t.oid)),
         'data_size', pg_size_pretty(pg_relation_size(t.oid)),
         'index_size', pg_size_pretty(pg_indexes_size(t.oid))
     ))
     FROM pg_class t
     JOIN pg_namespace n ON n.oid = t.relnamespace
     WHERE n.nspname = 'substrate'
       AND t.relkind IN ('r', 'p')
       AND t.relname IN ('entity', 'edge', 'edge_member', 'physicality',
                          'sequence', 'significance')
    ) AS storage_sizes,

    NOW() AS snapshot_at;
