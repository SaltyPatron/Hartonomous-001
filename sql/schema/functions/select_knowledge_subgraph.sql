-- substrate.select_knowledge_subgraph
--
-- Knowledge-selection vocab builder for Substrate Synthesis synthesis. Given a
-- seed set of entity hashes (e.g. user-supplied concept names resolved
-- via substrate.text_decompose) and a budget, BFS through substrate.edge_member
-- weighted by edge significance mu (per-arena-weighted union) to grow a
-- coherent subgraph of word_form entities the bear will know about.
--
-- Architectural intent (rule 35-inference-and-godel + AP-1 open arenas):
--   Vocab = the bear's brain contents — concepts the user wants it to know.
--   Domain-specific bears (medical, math, code) fall out trivially by varying
--   the seed set. Generic bears seed with high-degree function words.
--   MoE experts fall out per-seed-set (different seed = different expert).
--
-- Returns the BFS-discovered subgraph as (entity_hash, edge_count) rows
-- ordered by discovery order (seeds first, then BFS layer 1, layer 2, ...).
-- This ordering becomes the model's tokenizer index — vocab[0..N-1].
--
-- Parameters:
--   p_seed_hashes      : Initial concept hashes (substrate.entity rows).
--   p_arena_weights    : Per-arena weights as (code, weight) pairs.
--   p_vocab_budget     : Target vocab size; BFS stops when reached.
--   p_top_k_per_node   : Max neighbors to add per frontier node per iteration.
--   p_entity_type      : Filter to this entity type (default 'word_form').
--
-- Notes:
--   - Set-based BFS via recursive CTE — single query, no per-node round-trip.
--   - Edge weight = SUM_over_arenas(es.mu * weight_for_arena).
--   - Visited set is the result of the recursion; dedup is the WHERE NOT EXISTS guard.
CREATE OR REPLACE FUNCTION substrate.select_knowledge_subgraph(
    p_seed_hashes    BYTEA[],
    p_arena_weights  TEXT[],         -- alternating: arena_code, weight, arena_code, weight, ...
    p_arena_values   DOUBLE PRECISION[],
    p_vocab_budget   INT,
    p_top_k_per_node INT DEFAULT 32,
    p_entity_type    TEXT DEFAULT 'word_form'
)
RETURNS TABLE (
    entity_hash      BYTEA,
    discovery_round  INT,
    edge_count       BIGINT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_entity_type_id INT;
    v_round          INT := 0;
    v_added          INT;
BEGIN
    SELECT id INTO STRICT v_entity_type_id
      FROM substrate.entity_type WHERE code = p_entity_type;

    -- Initialize visited set with seeds (round 0).
    CREATE TEMP TABLE visited (
        entity_hash     BYTEA PRIMARY KEY,
        discovery_round INT NOT NULL,
        edge_count      BIGINT NOT NULL DEFAULT 0
    ) ON COMMIT DROP;

    INSERT INTO visited (entity_hash, discovery_round, edge_count)
    SELECT DISTINCT s.h, 0, 0
      FROM unnest(p_seed_hashes) AS s(h)
     WHERE EXISTS (
         SELECT 1 FROM substrate.entity_classification ec
          WHERE ec.entity_hash = s.h AND ec.entity_type_id = v_entity_type_id
     );

    -- Arena weight lookup table (small, in-memory).
    CREATE TEMP TABLE arena_weight (
        context_type_id INT PRIMARY KEY,
        weight          DOUBLE PRECISION NOT NULL
    ) ON COMMIT DROP;

    INSERT INTO arena_weight (context_type_id, weight)
    SELECT sc.id, COALESCE(w.weight, 1.0)
      FROM substrate.significance_context sc
      LEFT JOIN unnest(p_arena_weights, p_arena_values) AS w(arena, weight)
        ON w.arena = sc.code;

    -- BFS rounds. Each round picks top-K neighbors per current-frontier node
    -- by weighted edge mu, adds them to visited, repeats until budget filled.
    WHILE (SELECT count(*) FROM visited) < p_vocab_budget LOOP
        v_round := v_round + 1;

        WITH frontier AS (
            SELECT v.entity_hash AS hash
              FROM visited v
             WHERE v.discovery_round = v_round - 1
        ),
        -- Find edges where one endpoint is in the frontier.
        candidate_edges AS (
            SELECT em_self.edge_type_id, em_self.edge_hash, em_self.entity_hash AS self_h
              FROM frontier f
              JOIN substrate.edge_member em_self ON em_self.entity_hash = f.hash
        ),
        -- For each candidate edge, the OTHER participant is a vocab candidate.
        candidate_neighbors AS (
            SELECT em_other.entity_hash AS neighbor,
                   ce.edge_type_id,
                   ce.edge_hash
              FROM candidate_edges ce
              JOIN substrate.edge_member em_other
                ON em_other.edge_type_id = ce.edge_type_id
               AND em_other.edge_hash    = ce.edge_hash
               AND em_other.entity_hash != ce.self_h
        ),
        -- Score each neighbor by sum of weighted edge mu across arenas.
        scored AS (
            SELECT cn.neighbor,
                   sum(es.mu * aw.weight) AS score,
                   count(*) AS edge_count
              FROM candidate_neighbors cn
              JOIN substrate.edge_significance es
                ON es.edge_type_id = cn.edge_type_id
               AND es.edge_hash    = cn.edge_hash
              JOIN arena_weight aw ON aw.context_type_id = es.context_type_id
              JOIN substrate.entity_classification ec
                ON ec.entity_hash    = cn.neighbor
               AND ec.entity_type_id = v_entity_type_id
             WHERE NOT EXISTS (SELECT 1 FROM visited v WHERE v.entity_hash = cn.neighbor)
             GROUP BY cn.neighbor
        ),
        ranked AS (
            SELECT neighbor, score, edge_count,
                   ROW_NUMBER() OVER (ORDER BY score DESC, neighbor) AS rk
              FROM scored
        )
        INSERT INTO visited (entity_hash, discovery_round, edge_count)
        SELECT neighbor, v_round, edge_count
          FROM ranked
         WHERE rk <= LEAST(p_top_k_per_node, p_vocab_budget - (SELECT count(*)::INT FROM visited));

        GET DIAGNOSTICS v_added = ROW_COUNT;
        IF v_added = 0 THEN EXIT; END IF;  -- frontier exhausted
        IF v_round >= 32 THEN EXIT; END IF;  -- max-depth safety
    END LOOP;

    RETURN QUERY
    SELECT v.entity_hash, v.discovery_round, v.edge_count
      FROM visited v
     ORDER BY v.discovery_round, v.edge_count DESC, v.entity_hash;
END;
$$;

COMMENT ON FUNCTION substrate.select_knowledge_subgraph(BYTEA[], TEXT[], DOUBLE PRECISION[], INT, INT, TEXT) IS
    'Substrate Synthesis knowledge selection: BFS-expand a seed concept set through edge_member by arena-weighted edge mu. Vocab IS the bear''s brain contents. Domain-specific bears via seed-set variation; MoE experts per-seed-set.';
