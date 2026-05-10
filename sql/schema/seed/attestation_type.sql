-- Attestation types. Open vocabulary — runtime additions are expected.
--
-- Glicko-2 score (per docs/01-tensor-primitive-spec.md §V) and per-event
-- weight stratify what KIND of evidence is being recorded. Sign-bearing
-- (positive vs negative) attestation lives in the score parameter (1=win,
-- 0=loss); per-event weight scales the magnitude of the rating update.
--
-- Per-event weight defaults reflect evidence density vs confidence:
--   corpus co-occurrence: 0.1  (high-volume, low-per-event-confidence)
--   curated lexical:      1.0  (hand-curated)
--   tuple-level model evidence: 0.5-0.6 (the spec §IV mapping)
--   inference outcomes:   1.5  (sparse ground-truth signal)
--   expert correction:    2.0  (highest single-event impact)
INSERT INTO substrate.attestation_type (code, description, default_event_weight) VALUES
    -- Corpus / lexicon evidence
    ('corpus_co_occurrence_window',
     'Decomposer slid window of radius R over a parent text composition; per-pair weighted comparison event. Substrate analog of word2vec/GloVe statistics.',
     0.1),
    ('corpus_proximity_within_sentence',
     'Same as corpus_co_occurrence_window but strictly within a sentence boundary.',
     0.1),
    ('lexical_curated_relation',
     'Curated lexicon assertion (WordNet has_sense, Wiktionary etymology, OMW alignment, UD deprel labels). High per-event confidence.',
     1.0),
    ('lexical_attested_translation',
     'Bilingual lexicon entry or aligned-sentence translation pair (Tatoeba, OPUS).',
     0.8),
    -- Cross-source evidence
    ('cross_model_divergence',
     'Cross-model fireflies disagree; cell fragmented. Recorded with score=0.5 so sigma stays wide and the engine''s curiosity loop targets the gap.',
     0.5),
    -- Inference outcomes (Glicko Step-6 closed loop)
    ('inference_outcome_accept',
     'Inference Step 6: query path produced an answer the user/downstream-task accepted. Updates path edge_significance positively (score=1, high weight).',
     1.5),
    ('inference_outcome_reject',
     'Inference Step 6: query path produced an answer that was rejected. Negative event on the path (score=0, high weight).',
     1.5),
    ('expert_correction',
     'Human-in-loop override of an edge''s rating. Highest per-event weight; used sparingly for corrections that should dominate accumulated automatic evidence.',
     2.0),
    ('provenance_authority_corroboration',
     'Multi-source assertion resolved through provenance_edge_authority. Used when several provenances of differing trust priors agree on an edge''s rating.',
     0.8),
    -- Tuple-level model evidence (per docs/01-tensor-primitive-spec.md §IV).
    -- Each tuple shape produces its own attestation_type. Sign carried via
    -- Glicko score, magnitude via per-event weight.
    ('model_attention_qk_pattern',
     'AttentionBlock tuple Q×K^T projection between two content entities (token, image_patch, audio_frame).',
     0.6),
    ('model_attention_vo_pattern',
     'AttentionBlock tuple V·O^T projection between two content entities.',
     0.5),
    ('model_cross_modal_alignment',
     'CrossAttentionBlock tuple Q^T·K projection where Q-side and K-side bind to different content-entity-types (text↔image, text↔audio, decoder-token↔encoder-token).',
     0.5),
    ('model_ffn_full_path',
     'SwiGluFfn or BertFfn tuple full-path response: down(act(gate(x))⊙up(x)) or output(act(intermediate(x))) per content-entity pair.',
     0.5),
    ('model_input_embedding',
     'EmbeddingLookup table: per-row firefly POINTZM position + cosine between vocab token rows.',
     0.5),
    ('model_embedding_proximity',
     'Per-(model, token) firefly POINTZM position attestation on the word_form entity. Track-1 firefly geometry binding — entity_significance event recording where model M places token T in 4D space.',
     0.4),
    ('model_lm_head_projection',
     'LM head Linear (lm_head slot in EmbeddingLookup-dual): residual direction → output token logit.',
     0.5),
    ('model_layer_norm_evidence',
     'Normalization primitive γ/β contour stored as physicality on the tensor entity.',
     0.3),
    ('model_inference_state_evidence',
     'BnState tuple running_mean/running_var/num_batches_tracked — derived inference-time state, not learned content. Lower per-event weight.',
     0.2),
    ('model_local_kernel_evidence',
     'LocalKernel primitive (conv2d, conv1d, depthwise, pointwise) response between content-entity neighbors (pixel_region, audio_chunk).',
     0.4),
    ('model_position_embedding',
     'Position embedding (absolute / RoPE / ALiBi / Swin relative-position-bias-table): positional bias contribution.',
     0.3),
    ('model_moe_router',
     'MoeRouterBlock router: per-token routing strength alignment between tokens that route to the same expert.',
     0.4),
    ('model_moe_expert_response',
     'MoeRouterBlock expert: per-expert FFN response between content-entity pairs the expert refines together.',
     0.4),
    ('model_lora_adapter_evidence',
     'LoraDelta tuple: A·B low-rank update''s response on the same edges the base attests to. Stored alongside base attestations under a distinct attestation_type so synthesizers can choose to merge or keep separate.',
     0.5),
    ('model_codec_evidence',
     'EmbeddingLookup VQ codebook: per-codeword position attestation on codec_codevector entities.',
     0.4),
    ('model_detection_class_attestation',
     'DetectionHead class_proj: per-(object_query, visual_concept) class score.',
     0.5),
    ('model_detection_bbox_attestation',
     'DetectionHead bbox_proj: per-object_query bbox parameter prediction recorded as physicality on the object_query entity.',
     0.5),
    ('model_quantization_variant_evidence',
     'Same per-tuple evidence under a different quantization (FP8/AWQ/GPTQ/MXFP4). Lower per-event weight because lossy.',
     0.3);
