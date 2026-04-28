-- substrate.entity_sense INTENTIONALLY REMOVED.
--
-- Was redundant with the has_sense edge (lemma → synset). The substrate already
-- captures this binding once via substrate.edge_member; per-arena Glicko ratings
-- (mu, sigma, games) live on substrate.edge_significance keyed by that edge,
-- not on a parallel junction. Per-sense tag_count from WordNet's index.sense
-- maps to substrate.edge_significance.games for the lexical_disambiguation arena.
--
-- File retained as documentation; no longer @included from migration 0010.
SELECT 1; -- placeholder so this .sql file remains loadable if @included by accident.
