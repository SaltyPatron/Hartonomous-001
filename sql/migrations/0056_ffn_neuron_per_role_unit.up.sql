-- 0056_ffn_neuron_per_role_unit.up.sql
--
-- Per-role unit emission for FFN transformation tensors. The first concrete
-- realization of P5 from docs/build-plan.md "Corrected execution order" — the
-- per-role passes are the substrate's actual matmul-replacement (a Track-2
-- transformation tensor's row IS the learned function of one neuron;
-- decomposing the tensor into row-entities + typed edges with significance
-- IS the substrate's encoding of the model's inference behavior). The
-- recomposer scatters these per-role units into target tensors at distillation.
--
-- New entity type:
--   ffn_neuron — one entity per surviving (sparsity-filtered) row of any
--                FFN tensor (FFN_GATE, FFN_UP, FFN_DOWN, MOE_SHARED_EXPERT).
--                Hashed by row content only (the f64-canonicalized weight
--                vector). Same row content across models collapses to ONE
--                entity → cross-model FFN-neuron corroboration becomes
--                possible. Modality model_weights, partitioned by entity_type
--                via the existing entity_model partition (per migration 0006).
--
-- New edge type:
--   has_ffn_neuron — tensor → ffn_neuron. The placement (which neuron
--                index in this tensor) is recorded via substrate.sequence
--                row (parent=tensor, child=ffn_neuron, ordinal_position=row).
--                The projection role (gate/up/down/shared) is recoverable
--                from the source tensor's tensor_tensor_role junction.
--                The layer index is recoverable from the tensor's
--                in_layer edge (when populated by the model decomposer).
--
-- Sparsity: rows whose L2 magnitude is below the pass's threshold are
-- not emitted (Substrate Law #11). They encode no learned function.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('ffn_neuron', 'model_weights')
    ON CONFLICT (code) DO NOTHING;

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_ffn_neuron', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'ffn_neuron'))
    ON CONFLICT (code) DO NOTHING;

COMMENT ON COLUMN substrate.edge.edge_type_id IS
    'Reference to substrate.edge_type. Categories: structural, cross_lingual, cross_modal, unicode, model_derived. has_ffn_neuron (model_derived) carries per-role unit content edges from FFN tensors to their constituent ffn_neuron entities.';
