-- Entity types: 42 rows in canonical insertion order. SERIAL ids 1..42 must
-- match partition declarations in tables/core/entity_*.sql.
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
    ('codevector',               'model_weights');  -- 42
