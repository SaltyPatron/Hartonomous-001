-- 0044_tensor_name_edges.up.sql
--
-- Per the seed-uses-core rule (`.claude/rules/00-hartonomous-core.md`),
-- every text-bearing string in every decomposer must enter the substrate
-- through the text core decomposer's full DAG (codepoint → grapheme →
-- word_form → text_composition → document) so identical strings collapse
-- to ONE substrate document with TWO edges — not two independent strings.
--
-- The safetensors decomposer ingests two classes of internal text strings
-- that today never reach the substrate:
--
--   1. Per-tensor names ("model.layers.12.self_attn.q_proj.weight",
--      "blocks.0.attn.qkv.weight", etc.). These are how the tensor's role
--      and placement-in-architecture are named in the original safetensors
--      header. Without them the recomposer cannot reconstruct a valid
--      safetensors header on export.
--
--   2. The architecture class string ("BertModel", "Qwen2ForCausalLM",
--      "LlamaForCausalLM"). Different snapshots that share an architecture
--      class share these bytes; same content → same substrate document.
--
-- Both edges target `document` because TextDecomposer.IngestUtf8DocumentIntoBatch
-- always returns a document handle (one document per ingestion call). Per the
-- core rule "seed-uses-core" — a tensor name is a full text DAG, not a
-- raw-bytes hash on the tensor entity itself.
--
-- Category = 'structural'. New edges with assigned ids > 33 route into
-- substrate.edge_default per the partition layout in 0006.

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_tensor_name',         'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'document')),
    ('has_architecture_name',   'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'document'));
