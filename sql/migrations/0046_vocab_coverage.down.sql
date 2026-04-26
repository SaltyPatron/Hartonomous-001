-- 0046_vocab_coverage.down.sql
DELETE FROM substrate.edge_type WHERE code IN ('covers_lemma', 'has_vocab_coverage');
DELETE FROM substrate.entity_type WHERE code = 'vocab_coverage_profile';
