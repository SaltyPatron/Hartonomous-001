-- 0063_substrate_edge_decomposition.up.sql
--
-- The actual substrate-as-AI shape: tensor weights become TYPED EDGES
-- between substrate-identified residual-stream directions and per-role
-- units, weighted by the connection strength (encoded as mu offset on
-- substrate.significance). Forward pass = A* over these edges, not
-- matmul on the row-blobs.
--
-- residual_direction: one entity per (model_architecture, layer_index,
--   dim_index, role). The "input direction at layer L, dim D" of the
--   FFN-up matrix is the column basis the row reads from. Hashed by
--   (architecture_hash, layer_index, dim_index, role_tag).
--
-- ffn_input_edge: residual_direction → ffn_neuron. The neuron READS this
--   direction with strength encoded in the edge's significance mu (above
--   1500 = excitatory, below 1500 = inhibitory). Below the per-tensor
--   noise floor, NO EDGE EXISTS — Substrate Law #11.
--
-- ffn_output_edge: ffn_neuron → residual_direction. The neuron WRITES
--   to this direction. Same mu encoding.

INSERT INTO substrate.entity_type (code, modality) VALUES
    ('residual_direction', 'model_weights')
    ON CONFLICT (code) DO NOTHING;

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('ffn_input_edge', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'residual_direction'),
        (SELECT id FROM substrate.entity_type WHERE code = 'ffn_neuron')),
    ('ffn_output_edge', 'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'ffn_neuron'),
        (SELECT id FROM substrate.entity_type WHERE code = 'residual_direction'))
    ON CONFLICT (code) DO NOTHING;
