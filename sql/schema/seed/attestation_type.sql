-- 32 starter attestation types: 14 base evidence kinds + 18 per-role model
-- evidence kinds. Open vocabulary — runtime additions are expected (e.g.,
-- per-corpus or per-model arena-attestation pairs).
--
-- Per-role taxonomy (lines 63+) is what the safetensors model passes
-- reference: TokenCrossEdgePass uses model_input_embedding;
-- TokenAttentionEdgePass uses model_attention_qk_pattern; TokenFfnEdgePass
-- uses model_ffn_factor_alignment / model_ffn_full_path; PerRowContentPass
-- uses model_per_role_unit_circuit; EmbeddingFireflyPass uses
-- model_embedding_proximity. Without these rows the model decomposer's
-- substrate.resolve_attestation_type_id calls fail with NULL → the pass
-- raises 'attestation_type code=... missing — bootstrap not applied?'.
-- The seed must run BEFORE any safetensors phase (M7).
--
-- Per-event weight defaults reflect evidence density vs confidence:
--   curated lexical relations: 1.0 (one high-confidence event)
--   corpus co-occurrence: 0.1  (high-volume, low-per-event-confidence)
--   model attention/circuit: 0.5 (medium-volume, structural-confidence)
--   inference outcomes:    1.5 (sparse, ground-truth signal)
--   expert correction:     2.0 (highest single-event impact)
--
-- These are PRIORS. Per-emission weight overrides are passed through the
-- significance-event API at call time.
INSERT INTO substrate.attestation_type (code, description, default_event_weight) VALUES
    ('corpus_co_occurrence_window',
     'Decomposer slid window of radius R over a parent text composition; per-pair weighted comparison event. Weight scaled by 1/distance × parent_significance × 1/RLE_count. Substrate analog of word2vec/GloVe statistics.',
     0.1),
    ('corpus_proximity_within_sentence',
     'Same as corpus_co_occurrence_window but strictly confined within a sentence boundary (no cross-sentence pairs). Used when sentence-level decomposition is the natural unit.',
     0.1),
    ('lexical_curated_relation',
     'Curated lexicon assertion (WordNet has_sense, Wiktionary etymology, OMW alignment, UD deprel labels). High per-event confidence because hand-curated.',
     1.0),
    ('lexical_attested_translation',
     'Bilingual lexicon entry or aligned-sentence translation pair (Tatoeba, OPUS). One attestation per parallel pair.',
     0.8),
    ('model_embedding_proximity',
     'Cosine/magnitude of two tokens'' rows in a decomposed model''s embedding or unembedding matrix. Track-1 firefly geometry binding.',
     0.4),
    ('model_attention_pattern',
     'Attention head''s Q×K projection peak between two existing token entities. Track-2 per-role-unit attestation expressed as a direct token↔token edge.',
     0.5),
    ('model_ffn_factor_alignment',
     'FFN per-role unit''s input/output projection alignment with two existing token entities. Track-2 attestation.',
     0.5),
    ('model_per_role_unit_circuit',
     'Identified circuit binding per-role units (substrate entities) to a relation between existing token entities. Bridge edges queries_from/attends_to_class/projects_to.',
     0.6),
    ('cross_model_corroboration',
     'Voronoi-cell tightness or Fréchet-trajectory similarity between per-role units across two or more decomposed models. Cross-architecture consensus event.',
     0.7),
    ('cross_model_divergence',
     'Cross-model fireflies disagree; cell fragmented. Recorded as negative-evidence event so Glicko sigma stays wide and the engine''s curiosity loop targets the gap.',
     0.5),
    ('inference_outcome_accept',
     'Step 6 of inference: query path produced an answer the user/downstream-task accepted. Updates the path''s edge_significance positively. Closes the OODA loop.',
     1.5),
    ('inference_outcome_reject',
     'Step 6: query path produced an answer that was rejected. Updates the path''s edge_significance negatively (loss event).',
     1.5),
    ('expert_correction',
     'Human-in-loop override of an edge''s rating. Highest per-event weight; used sparingly for corrections that should dominate accumulated automatic evidence.',
     2.0),
    ('provenance_authority_corroboration',
     'Multi-source assertion resolved through provenance_edge_authority. Used when several provenances of differing trust priors agree on an edge''s rating.',
     0.8),

    -- Per-role attestation taxonomy. Each kind of model component that
    -- can attest to a token-pair relationship gets its own attestation_type.
    -- Layer/head/position indices are metadata on the individual attestation
    -- event, NOT separate types — that would explode the vocabulary. The
    -- taxonomy here is at the level of "what computational role of the
    -- model is producing this evidence."
    ('model_attention_query_projection',
     'Attention head Q-side projection: token T appears as a query when bound to key tokens with this attention weight. Per-(layer, head) details on the attestation row.',
     0.5),
    ('model_attention_key_projection',
     'Attention head K-side projection: token S appears as a key when responded to by query tokens with this attention weight.',
     0.5),
    ('model_attention_value_projection',
     'Attention head V-side projection: when key token S is attended, this is the value contribution.',
     0.5),
    ('model_attention_output_projection',
     'Attention head O-side projection: residual contribution mapped through the head''s output transform.',
     0.5),
    ('model_attention_qk_pattern',
     'Combined Q×K^T pattern between two tokens — what the head encodes about the token pair''s mutual attention. Strongest single-attestation kind for token-pair relationships from attention.',
     0.6),
    ('model_attention_vo_pattern',
     'Combined V×O^T pattern between two tokens — what the head produces in residual when one attends to the other.',
     0.5),
    ('model_ffn_up_projection',
     'FFN up-projection (input → hidden): token T''s residual direction activates which FFN dimensions.',
     0.4),
    ('model_ffn_gate_projection',
     'FFN gate-projection (SwiGLU/GeGLU): token T''s gate activation pattern.',
     0.4),
    ('model_ffn_down_projection',
     'FFN down-projection (hidden → output): which output token directions an FFN dimension produces.',
     0.4),
    ('model_ffn_full_path',
     'Full FFN path up → activation → down composing as a token-T-to-token-U attestation.',
     0.5),
    ('model_lm_head_projection',
     'LM head / unembedding: residual direction → output token logit.',
     0.5),
    ('model_input_embedding',
     'Input embedding row: token → its hidden-space representation. Source of all downstream model_embedding_proximity attestations between token pairs.',
     0.5),
    ('model_layer_norm_evidence',
     'Layer norm scale evidence (per-dimension parameter). Recorded on per-layer model_architecture attestations rather than token-token edges.',
     0.3),
    ('model_moe_router',
     'MoE router scoring: token T''s routing weight to expert E.',
     0.4),
    ('model_moe_expert_response',
     'MoE expert response: when expert E activates, which token pairs does it relate.',
     0.4),
    ('model_lora_adapter_evidence',
     'LoRA adapter contribution: A·B low-rank update''s token-pair contribution.',
     0.5),
    ('model_position_embedding',
     'Position embedding (absolute / RoPE / ALiBi) evidence: positional bias on token-pair attention.',
     0.3),
    ('model_quantization_variant_evidence',
     'Same per-role evidence under a different quantization (FP8/AWQ/GPTQ/MXFP4). Lower-trust per-event because lossy.',
     0.3);
