-- Attestation types — generic, sign-discriminating only.
--
-- P1d (2026-05-14 architectural correction): the prior 27 modality-specific
-- rows (model_attention_qk_pattern, model_ffn_full_path, model_lm_head_projection,
-- model_cross_modal_alignment, corpus_co_occurrence_window, lexical_curated_relation,
-- etc.) pidgeonholed the universal substrate into a finite enumeration that
-- had to be extended every time a new modality / model mechanism / source
-- kind appeared. The (provenance × arena) tuple already discriminates
-- evidence by source and by domain — adding a third discrimination axis was
-- redundant AND broke the universal-substrate property because every new
-- source would need an attestation_type extension.
--
-- The substrate's invention rule: every claim about content from every
-- source is the same shape of evidence. ONE Glicko-2 attestation surface
-- (substrate.edge_significance + substrate.entity_significance), with
-- discrimination via:
--   * provenance_id   — WHICH source attested (wordnet / wiktionary / ud /
--                       tatoeba / each ingested AI model / user_session / etc.)
--   * context_type_id — IN WHICH arena (lexical_disambiguation /
--                       syntactic_role_fitness / domain-specific arenas / etc.)
--   * score           — Glicko-2 win/loss/draw (1.0 / 0.0 / 0.5)
--   * weight          — per-event weight magnitude (caller-controlled,
--                       defaults below)
--
-- attestation_type now carries ONLY the sign-bearing discriminator. The
-- column on substrate.edge_significance + substrate.entity_significance is
-- on the removal path (P1e) — once IngestionBatch.AddSignificance and all
-- decomposer callers stop threading it, the column will drop and these
-- three rows become unused infrastructure.
--
-- AP-31 (sign is load-bearing): Glicko score = value > 0 ? 1.0 : 0.0;
-- weight = Math.Abs(value). Caller emits positive_evidence with
-- score=1 OR negative_evidence with score=0; neutral_evidence with
-- score=0.5 widens sigma without moving mu (cross-source divergence /
-- inconclusive signal).
INSERT INTO substrate.attestation_type (code, description, default_event_weight) VALUES
    ('positive_evidence',
     'Sign-positive attestation event. score=1.0 in Glicko-2 update. weight = caller-supplied magnitude (default 1.0).',
     1.0),
    ('negative_evidence',
     'Sign-negative attestation event. score=0.0 in Glicko-2 update. Used for anti-correlation, suppression, antipodal, antonym, rejection-of-inference-path. weight = caller-supplied magnitude.',
     1.0),
    ('neutral_evidence',
     'Sign-neutral attestation event. score=0.5 in Glicko-2 update. Widens sigma without moving mu — cross-source divergence, inconclusive signal, multi-model disagreement. weight = caller-supplied magnitude.',
     0.5);
