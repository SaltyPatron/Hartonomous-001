-- substrate.resolve_context_id(p_code TEXT)
--
-- Translate a significance_context code (e.g. 'lexical_disambiguation',
-- 'semantic_relevance') to its INT id. Single-row lookup used by C# call
-- sites that translate arena codes to ids before invoking
-- substrate.record_comparison / record_corroboration / prune_significance.
--
-- Arenas are open-vocabulary (.claude/rules/15 § "Arenas are open-
-- vocabulary"). Code that hard-codes the 10 starter codes is wrong (AP-1);
-- this resolver works for any code present in substrate.significance_context.
--
-- Returns NULL when the code does not exist. Callers MUST handle NULL
-- (the C# updater raises InvalidOperationException with the unknown code).
CREATE OR REPLACE FUNCTION substrate.resolve_context_id(p_code TEXT)
RETURNS INT
LANGUAGE sql STABLE
AS $$
    SELECT id
      FROM substrate.significance_context
     WHERE code = p_code;
$$;

COMMENT ON FUNCTION substrate.resolve_context_id(TEXT) IS
    'Resolve a significance_context.code to its INT id. Returns NULL if unknown. STABLE — safe to inline in larger queries.';
