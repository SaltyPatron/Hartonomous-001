-- 0040_prime_edge_significance.down.sql

DELETE FROM substrate.significance
 WHERE edge_id IS NOT NULL
   AND context_type_id IN (
       SELECT id FROM substrate.significance_context
        WHERE code IN ('semantic_relevance', 'lexical_disambiguation')
   )
   AND games = 0;
