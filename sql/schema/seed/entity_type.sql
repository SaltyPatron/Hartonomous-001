-- Entity types: 54 rows in canonical insertion order. SERIAL ids 1..42 are
-- pinned to the entity_model named partition (tables/core/entity_model.sql).
-- IDs 43..54 (per-role-unit families added for the analysis-pass DAG —
-- attention components, conv/codec filters, MoE expert neurons + route
-- directions, vision/conformer/diffusion/LoRA/modality/object-query/bbox/
-- class-head per-row units) currently route through entity_default. A
-- follow-up migration can detach/extend entity_model's FOR VALUES list to
-- bring them under the named partition for index locality.
--
-- Content-kind only. No dataset-named, algorithm-named, or relation-shaped
-- types. The substrate's identity model (BLAKE3 over content) requires that
-- the same content collapses to the same hash regardless of source.
--
-- Track 1 — text/audio/image/video content:
--   codepoint, grapheme_cluster, word_form, morpheme, lemma,
--   text_composition, paragraph, document, synset,
--   collation_element, language_name,
--   pixel_region, audio_recording, audio_chunk, video_frame.
--
-- Track 2 — model decomposition. Per .claude/rules/35-inference-and-godel.md
-- the per-role unit entities are what carry a model's learned function. The
-- substrate doesn't store every weight verbatim — it extracts the activated
-- semantic paths (lottery-ticket subnetwork) and discards gradient noise per
-- Law #11 (sparsity is honest recording, not approximation).
--
--   tensor                   — one safetensors entry (a whole weight matrix)
--   model_architecture       — a model's architecture identity
--   tokenizer_model          — a tokenizer instance (vocab + merges + config)
--   attention_pattern        — Q/K/V/O contribution pattern across heads
--   attention_head           — one head of multi-head attention
--   attention_archetype      — canonical pattern an attention layer encodes
--   embedding_position       — one row of the token embedding matrix (firefly)
--   ffn_neuron               — one output neuron of an FFN layer
--   logit_projection         — one row of the output unembedding matrix
--   moe_route                — a single MoE router decision
--   moe_routing_profile      — aggregate routing distribution for an MoE layer
--   residual_direction       — a principal direction in residual-stream geometry
--   archetype                — a canonical pattern that a tensor encodes
--
-- Per-tensor analysis surfaces (each is a content-addressed reduction of a
-- whole tensor's structure; identical reductions across models dedup):
--   sparsity_profile         — log-magnitude bucket histogram + near-zero fraction
--   weight_distribution      — distribution shape (mean, std, percentiles, etc.)
--   eigenvalue_spectrum      — top-K eigenvalues of weight covariance
--   svd_spectrum             — singular value spectrum
--   svd_rank_component       — one rank-1 component (u_i ⊗ v_i⊤)
--   activation_range         — observed min/max/mean activation per row/col
--   layer_norm_scale         — per-feature layer-norm γ scale parameter set
--   layer_similarity_pair    — pair of tensors with measured similarity
--   rope_freq_table          — RoPE frequency table for an attention layer
--   codec_codebook           — quantization codebook (AWQ/GPTQ etc.)
--   codec_codevector         — single centroid in a quantization codebook
--   vocab_coverage_profile   — tokenizer vocab coverage of the lemma seed
--
-- Removed (vs the original 25-row schema, none re-added):
--   ud_sentence, ud_token, tatoeba_sentence, word_sense, wikt_sense,
--   bpe_token, inflected_form. See prior version comments.
--
-- ARCHITECTURAL CORRECTION (2026-05-08): The per-role-unit / per-tensor-
-- analysis entity types below (ids 19-54 except 16/17/18) are PHANTOMS from
-- earlier framing where every model component became its own entity. Per
-- the user-stated invention (every model calculation = attestation_type on
-- edges between EXISTING token entities), these are deprecated:
--
--   Phantom entity types (deprecated; should become attestation_types on
--   token↔token edges in the per-role attestation taxonomy seeded in
--   sql/schema/seed/attestation_type.sql):
--     attention_pattern, attention_head, attention_archetype,
--     embedding_position, ffn_neuron, logit_projection, moe_route,
--     moe_routing_profile, residual_direction, archetype,
--     svd_rank_component, codec_codevector, codevector,
--     audio_codec_filter, bbox_projection, class_projection,
--     conformer_component, conv_filter, diffusion_component,
--     lora_component, modality_basis_vector, moe_expert_neuron,
--     moe_route_direction, object_query_slot, vision_feature_direction
--
--   Stay (per-tensor-level analysis surfaces — properly attached to the
--   tensor entity, not to phantom row-level entities. Migrating these to
--   physicality on the tensor entity is a follow-up):
--     sparsity_profile, weight_distribution, eigenvalue_spectrum,
--     svd_spectrum, activation_range, layer_norm_scale,
--     layer_similarity_pair, rope_freq_table, codec_codebook, codebook,
--     vocab_coverage_profile
--
--   Real structural artifacts (keep):
--     tensor, model_architecture, tokenizer_model
--
-- The per-role attestation_types added in attestation_type.sql
-- (model_attention_qk_pattern, model_ffn_full_path, model_input_embedding,
-- model_lm_head_projection, etc.) are the replacement: they live on edges
-- between token (word_form) entities, NOT as separate entity types.
--
-- Until decomposer passes are rewritten to emit token↔token edges instead
-- of phantom entities, the rows below remain in the seed so existing code
-- that looks up these codes doesn't crash. Code that creates these phantom
-- entities is on the deprecation path (see AP-21 in
-- .claude/rules/45-anti-patterns.md and the architectural correction
-- note 2026-05-08).
INSERT INTO substrate.entity_type (code, modality) VALUES
    ('codepoint',                'text'),           --  1
    ('grapheme_cluster',         'text'),           --  2
    ('word_form',                'text'),           --  3
    ('morpheme',                 'text'),           --  4
    ('lemma',                    'text'),           --  5
    ('text_composition',         'text'),           --  6
    ('paragraph',                'text'),           --  7
    ('document',                 'text'),           --  8
    ('synset',                   'text'),           --  9
    ('collation_element',        'text'),           -- 10
    ('language_name',            'text'),           -- 11
    ('pixel_region',             'image'),          -- 12
    ('audio_recording',          'audio'),          -- 13
    ('audio_chunk',              'audio'),          -- 14
    ('video_frame',              'video'),          -- 15
    ('tensor',                   'model_weights'),  -- 16
    ('model_architecture',       'model_weights'),  -- 17
    ('tokenizer_model',          'model_weights'),  -- 18
    ('attention_pattern',        'model_weights'),  -- 19
    ('attention_head',           'model_weights'),  -- 20
    ('attention_archetype',      'model_weights'),  -- 21
    ('embedding_position',       'model_weights'),  -- 22
    ('ffn_neuron',               'model_weights'),  -- 23
    ('logit_projection',         'model_weights'),  -- 24
    ('moe_route',                'model_weights'),  -- 25
    ('moe_routing_profile',      'model_weights'),  -- 26
    ('residual_direction',       'model_weights'),  -- 27
    ('archetype',                'model_weights'),  -- 28
    ('sparsity_profile',         'model_weights'),  -- 29
    ('weight_distribution',      'model_weights'),  -- 30
    ('eigenvalue_spectrum',      'model_weights'),  -- 31
    ('svd_spectrum',             'model_weights'),  -- 32
    ('svd_rank_component',       'model_weights'),  -- 33
    ('activation_range',         'model_weights'),  -- 34
    ('layer_norm_scale',         'model_weights'),  -- 35
    ('layer_similarity_pair',    'model_weights'),  -- 36
    ('rope_freq_table',          'model_weights'),  -- 37
    ('codec_codebook',           'model_weights'),  -- 38
    ('codec_codevector',         'model_weights'),  -- 39
    ('vocab_coverage_profile',   'model_weights'),  -- 40
    ('codebook',                 'model_weights'),  -- 41
    ('codevector',               'model_weights'),  -- 42
    -- Per-role-unit entity types for analysis passes (see edge_type.sql for
    -- the matching has_* edge codes that bind a tensor to its rows). Each
    -- row is content-hashed via PerRowContentPass canonical f64 encoding so
    -- identical row content across models / shards collapses to ONE entity.
    -- (AttentionComponentPass reuses attention_pattern (id 19) as its entity
    -- type — has_attention_component is the edge that bears the binding.)
    ('audio_codec_filter',       'model_weights'),  -- 43 (audio codec filter row)
    ('bbox_projection',          'model_weights'),  -- 44 (detection bbox-head projection)
    ('class_projection',         'model_weights'),  -- 45 (classification head projection)
    ('conformer_component',      'model_weights'),  -- 46 (conformer block component row)
    ('conv_filter',              'model_weights'),  -- 47 (per-channel conv filter)
    ('diffusion_component',      'model_weights'),  -- 48 (diffusion U-Net component row)
    ('lora_component',           'model_weights'),  -- 49 (LoRA adapter rank-1 component)
    ('modality_basis_vector',    'model_weights'),  -- 50 (cross-modal basis direction)
    ('moe_expert_neuron',        'model_weights'),  -- 51 (per-expert FFN neuron)
    ('moe_route_direction',      'model_weights'),  -- 52 (router gate direction)
    ('object_query_slot',        'model_weights'),  -- 53 (object-detection query slot)
    ('vision_feature_direction', 'model_weights');  -- 54 (vision-feature row direction)
