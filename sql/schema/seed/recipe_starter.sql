-- App-tier starter recipes seeded at db-bootstrap. Each recipe is the
-- factory default architectural fingerprint for the famous model family,
-- registered as substrate-content via substrate.save_recipe so practitioners
-- can resolve "--recipe-name minilm-base" without first having to ingest
-- a model. The recipe entity_hash is BLAKE3 of the canonical JSON payload;
-- if a practitioner ingests the actual model file later and the canonical
-- JSON matches byte-for-byte, the substrate.entity row dedups via
-- ON CONFLICT DO NOTHING (cross-source consensus: the app-starter ingest
-- and the real-model ingest converge to the same content-addressed row).
--
-- These canonical JSONs MUST be kept canonical (RFC 8785-style: sorted
-- object keys, no insignificant whitespace, normalized numbers) so the
-- BLAKE3 is stable across machines / OS / locale. The
-- src/Hartonomous.Recomposers/Synthesizers/RecipeTemplates.cs file holds
-- the C# representation; this seed mirrors it byte-for-byte in canonical
-- JSON form for the substrate side.

-- minilm-base: BertForMaskedLM, hidden=384, layers=6, heads=12.
-- (Matches RecipeTemplates.MiniLmBase().)
SELECT substrate.save_recipe(
    convert_to(
        '{"name":"minilm-base","architecture":{"family":"minilm","hf_architecture_name":"BertForMaskedLM","vocab_size":30000,"hidden_dim":384,"num_hidden_layers":6,"num_attention_heads":12,"num_key_value_heads":12,"head_dim":32,"intermediate_size":1536,"max_position_embeddings":512,"tie_word_embeddings":true,"activation":"gelu","norm_type":"layernorm","norm_eps":1e-12}}'::TEXT,
        'UTF8'),
    'minilm-base',
    'app_starter',
    'recipe');

-- bert-base: BertForMaskedLM, hidden=768, layers=12, heads=12.
SELECT substrate.save_recipe(
    convert_to(
        '{"name":"bert-base","architecture":{"family":"bert","hf_architecture_name":"BertForMaskedLM","vocab_size":30522,"hidden_dim":768,"num_hidden_layers":12,"num_attention_heads":12,"num_key_value_heads":12,"head_dim":64,"intermediate_size":3072,"max_position_embeddings":512,"tie_word_embeddings":false,"activation":"gelu","norm_type":"layernorm","norm_eps":1e-12}}'::TEXT,
        'UTF8'),
    'bert-base',
    'app_starter',
    'recipe');

-- llama-1b: LlamaForCausalLM, hidden=2048, layers=16, heads=32, GQA(32→8).
SELECT substrate.save_recipe(
    convert_to(
        '{"name":"llama-1b","architecture":{"family":"llama","hf_architecture_name":"LlamaForCausalLM","vocab_size":128256,"hidden_dim":2048,"num_hidden_layers":16,"num_attention_heads":32,"num_key_value_heads":8,"head_dim":64,"intermediate_size":8192,"max_position_embeddings":131072,"tie_word_embeddings":true,"activation":"silu","norm_type":"rmsnorm","norm_eps":1e-5,"rope":{"enabled":true,"theta":500000.0}}}'::TEXT,
        'UTF8'),
    'llama-1b',
    'app_starter',
    'recipe');

-- llama-3b: LlamaForCausalLM, hidden=3072, layers=28, heads=24, GQA(24→8).
SELECT substrate.save_recipe(
    convert_to(
        '{"name":"llama-3b","architecture":{"family":"llama","hf_architecture_name":"LlamaForCausalLM","vocab_size":128256,"hidden_dim":3072,"num_hidden_layers":28,"num_attention_heads":24,"num_key_value_heads":8,"head_dim":128,"intermediate_size":8192,"max_position_embeddings":131072,"tie_word_embeddings":true,"activation":"silu","norm_type":"rmsnorm","norm_eps":1e-5,"rope":{"enabled":true,"theta":500000.0}}}'::TEXT,
        'UTF8'),
    'llama-3b',
    'app_starter',
    'recipe');

-- mistral-7b: MistralForCausalLM, hidden=4096, layers=32, heads=32, GQA(32→8).
SELECT substrate.save_recipe(
    convert_to(
        '{"name":"mistral-7b","architecture":{"family":"mistral","hf_architecture_name":"MistralForCausalLM","vocab_size":32768,"hidden_dim":4096,"num_hidden_layers":32,"num_attention_heads":32,"num_key_value_heads":8,"head_dim":128,"intermediate_size":14336,"max_position_embeddings":32768,"tie_word_embeddings":false,"activation":"silu","norm_type":"rmsnorm","norm_eps":1e-5,"rope":{"enabled":true,"theta":1000000.0}}}'::TEXT,
        'UTF8'),
    'mistral-7b',
    'app_starter',
    'recipe');

-- qwen-7b (Qwen2.5-7B base): Qwen2ForCausalLM, hidden=3584, layers=28,
-- heads=28, GQA(28→4). Matches RecipeTemplates.Qwen7B().
SELECT substrate.save_recipe(
    convert_to(
        '{"name":"qwen-7b","architecture":{"family":"qwen2","hf_architecture_name":"Qwen2ForCausalLM","vocab_size":152064,"hidden_dim":3584,"num_hidden_layers":28,"num_attention_heads":28,"num_key_value_heads":4,"head_dim":128,"intermediate_size":18944,"max_position_embeddings":32768,"tie_word_embeddings":false,"activation":"silu","norm_type":"rmsnorm","norm_eps":1e-6,"rope":{"enabled":true,"theta":1000000.0}}}'::TEXT,
        'UTF8'),
    'qwen-7b',
    'app_starter',
    'recipe');

-- qwen-2.5-coder-3b: Qwen2ForCausalLM (Coder fine-tune), hidden=2048,
-- layers=36, heads=16, GQA(16→2). Matches the actual config.json from
-- /vault/models/models--Qwen--Qwen2.5-Coder-3B-Instruct/.
-- Hand-authored fingerprint so the recipe is queryable BEFORE
-- SafetensorsDecomposer learns to auto-emit one; once it does, an ingest
-- of the actual model will produce the SAME entity_hash via ON CONFLICT
-- DO NOTHING (if the canonical JSON matches byte-for-byte).
SELECT substrate.save_recipe(
    convert_to(
        '{"name":"qwen-2.5-coder-3b","architecture":{"family":"qwen2","hf_architecture_name":"Qwen2ForCausalLM","vocab_size":151936,"hidden_dim":2048,"num_hidden_layers":36,"num_attention_heads":16,"num_key_value_heads":2,"head_dim":128,"intermediate_size":11008,"max_position_embeddings":32768,"tie_word_embeddings":true,"activation":"silu","norm_type":"rmsnorm","norm_eps":1e-6,"rope":{"enabled":true,"theta":1000000.0}}}'::TEXT,
        'UTF8'),
    'qwen-2.5-coder-3b',
    'app_starter',
    'recipe');
