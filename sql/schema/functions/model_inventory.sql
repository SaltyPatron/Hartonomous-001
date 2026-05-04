-- substrate.model_inventory(p_model_arch_hash bytea)
--
-- Inventory of an ingested model's substrate state. V1 surface returns
-- counts that are reliably computable from the existing ingestion-time
-- substrate without name-parsing or junction-row population:
--
--   tensor_count                   total tensors via has_tensor edges
--   architectural_classification   total Track 2 architectural-classification
--                                  edges (attention_head_in_layer / ffn_*_in_layer
--                                  / vocab_embedding / etc.)
--   per_role_unit_count            per-role units bound to this model's tensors
--                                  (attention_pattern, ffn_neuron, embedding_position,
--                                  logit_projection, moe_expert_neuron, etc.)
--   embedding_firefly_count        Track 1 fireflies attached to token entities
--                                  reachable from this model
--
-- Layer / head / expert counts are NOT included until
-- substrate.tensor_position_index (migration 0037) is populated by the
-- decomposer (deferred until IIngestionBatch grows AddTensorPositionIndex).
-- The legacy approach of decoding edge_member.role_position is incorrect:
-- role_position is for ordering participants WITHIN AN EDGE, not content
-- placement. See migration 0037's commentary.
DROP FUNCTION IF EXISTS substrate.model_inventory(bytea);
CREATE OR REPLACE FUNCTION substrate.model_inventory(p_model_arch_hash bytea)
RETURNS TABLE (
    metric_code text,
    metric_value bigint,
    metric_detail text
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    -- Tensor count: tensors bound to this model_architecture via has_tensor.
    SELECT 'tensor_count'::text,
           count(DISTINCT em_tgt.entity_hash)::bigint,
           NULL::text
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.code = 'has_tensor'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_src.entity_hash = p_model_arch_hash

    UNION ALL

    -- Architectural classification edges (Track 2 V1 vocabulary).
    SELECT 'architectural_classification'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.edge_member em_tgt
      JOIN substrate.edge_type et      ON et.id = em_tgt.edge_type_id
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_tgt.entity_hash = p_model_arch_hash
       AND et.code IN (
            'attention_head_in_layer',
            'ffn_up_in_layer','ffn_gate_in_layer','ffn_down_in_layer',
            'residual_stream_position',
            'vocab_embedding','vocab_unembedding',
            'tokenizer_belongs_to_model',
            'position_encoding_for_layer',
            'layer_norm_for_layer_position',
            'tensor_in_model_at_position',
            'expert_in_moe_router','moe_router_for_layer','shared_expert_in_layer',
            'vision_feature_path','object_query_in_layer',
            'vision_classification_head','vision_localization_head',
            'cross_modal_attention',
            'audio_feature_path','audio_to_text_attention',
            'pipeline_component_of_model'
       )

    UNION ALL

    -- Per-role unit count: per-row analysis-pass entities (attention_pattern,
    -- ffn_neuron, embedding_position, logit_projection, moe_expert_neuron,
    -- etc.) bound to this model's tensors. Counts via the has_*_component /
    -- has_ffn_neuron / has_embedding_position / etc. edges that the existing
    -- analysis passes emit.
    SELECT 'per_role_unit_count'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.edge_member em_tensor_src
      JOIN substrate.edge_type et_has_tensor
        ON et_has_tensor.id = em_tensor_src.edge_type_id
       AND et_has_tensor.code = 'has_tensor'
      JOIN substrate.edge_role er_src
        ON er_src.id = em_tensor_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tensor_tgt
        ON em_tensor_tgt.edge_type_id = em_tensor_src.edge_type_id
       AND em_tensor_tgt.edge_hash    = em_tensor_src.edge_hash
      JOIN substrate.edge_role er_tgt
        ON er_tgt.id = em_tensor_tgt.edge_role_id AND er_tgt.code = 'target'
      JOIN substrate.edge_member em_unit_src
        ON em_unit_src.entity_hash = em_tensor_tgt.entity_hash
      JOIN substrate.edge_type et_has_unit
        ON et_has_unit.id = em_unit_src.edge_type_id
       AND et_has_unit.code IN (
            'has_attention_component','has_ffn_neuron','has_embedding_position',
            'has_logit_projection','has_moe_neuron','has_route_direction',
            'has_object_query','has_vision_feature','has_class_projection',
            'has_bbox_projection','has_codec_filter','has_conformer_component',
            'has_conv_filter','has_diffusion_component','has_lora_component',
            'has_modality_basis','has_layer_norm_scale','has_rope_freqs',
            'has_rank_component','has_moe_routing'
       )
     WHERE em_tensor_src.entity_hash = p_model_arch_hash

    UNION ALL

    -- Firefly count: Track 1 embedding_firefly physicalities on any
    -- substrate entity reachable from this model via entity_model_source.
    -- The substrate mechanic is universal — fireflies attach to whatever
    -- content-addressed entity the Laplacian-eigenmap projection landed on,
    -- regardless of classification (word_form / bpe_token / codepoint /
    -- pixel_region / audio_chunk / video_frame / lemma / synset / etc.).
    -- The query is modality- and language-agnostic by design.
    SELECT 'embedding_firefly_count'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'embedding_firefly'
      JOIN substrate.entity_model_source ems_entity
        ON ems_entity.entity_hash = p.entity_hash
      JOIN substrate.entity_model_source ems_arch
        ON ems_arch.model_source_id = ems_entity.model_source_id
       AND ems_arch.entity_hash = p_model_arch_hash;
$$;

COMMENT ON FUNCTION substrate.model_inventory(bytea) IS
    'Inventory of an ingested model: tensor count, architectural-classification edge count, per-role unit count, firefly count. Layer/head/expert counts deferred until tensor_position_index junction is populated.';
