using System.Text.RegularExpressions;
using Hartonomous.Core.Operations;
using Hartonomous.Decomposers.Safetensors.Packages;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Adapters;

public sealed partial class DenseLlmAdapter : BaseArchitectureAdapter
{
    private static readonly HashSet<string> RecognizedArchitectures = new(StringComparer.Ordinal)
    {
        "LlamaForCausalLM",
        "Qwen2ForCausalLM",
        "Qwen3ForCausalLM",
        "Qwen2_5ForCausalLM",
        "MistralForCausalLM",
        "GPTNeoXForCausalLM",
        "GPT2LMHeadModel",
        "GPTBigCodeForCausalLM",
        "DeepseekV2ForCausalLM",
    };

    private static readonly HashSet<string> RecognizedModelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "llama",
        "qwen2",
        "qwen3",
        "qwen2_5",
        "mistral",
        "gpt2",
        "gptneox",
        "gpt_neox",
        "gpt_bigcode",
        "deepseek_v2",
    };

    private static readonly string[] RequiredPaths =
    [
        "hidden_size",
        "num_attention_heads",
        "num_hidden_layers",
        "vocab_size",
    ];

    public DenseLlmAdapter(ILogger<DenseLlmAdapter> logger)
        : base(logger)
    {
    }

    public override string ArchitectureClassCode => "dense_llm";

    public override IReadOnlyList<string> RequiredConfigPaths => RequiredPaths;

    public override bool CanHandle(IConfigSnapshot config)
    {
        IReadOnlyList<string>? architectures = config.GetStringArray("architectures");
        bool architectureMatch = false;
        bool isDeepseekV2 = false;
        if (architectures is not null)
        {
            for (int i = 0; i < architectures.Count; i++)
            {
                string entry = architectures[i];
                if (RecognizedArchitectures.Contains(entry))
                {
                    architectureMatch = true;
                    if (string.Equals(entry, "DeepseekV2ForCausalLM", StringComparison.Ordinal))
                    {
                        isDeepseekV2 = true;
                    }
                    break;
                }
            }
        }

        if (!architectureMatch)
        {
            string modelType = config.GetString("model_type", string.Empty) ?? string.Empty;
            if (modelType.Length > 0 && RecognizedModelTypes.Contains(modelType))
            {
                architectureMatch = true;
                if (string.Equals(modelType, "deepseek_v2", StringComparison.OrdinalIgnoreCase))
                {
                    isDeepseekV2 = true;
                }
            }
        }

        if (!architectureMatch)
        {
            return false;
        }

        if (isDeepseekV2)
        {
            int? routedExperts = config.GetInt32("n_routed_experts");
            if (routedExperts is int n && n > 0)
            {
                return false;
            }
        }

        return true;
    }

    protected override (ModalityLobe Lobe, string Role)? ClassifyCore(string tensorName, int[] shape, string dtype)
    {
        if (tensorName == "model.embed_tokens.weight" ||
            tensorName == "transformer.wte.weight" ||
            tensorName == "embed_tokens.weight")
        {
            return (ModalityLobe.TextEmbedding, "token_embedding");
        }

        if (tensorName == "model.embed_positions.weight" ||
            tensorName == "transformer.wpe.weight")
        {
            return (ModalityLobe.TextPositionRope, "learned_position_embedding");
        }

        if (tensorName == "lm_head.weight")
        {
            return (ModalityLobe.TextLmHead, "lm_head");
        }

        if (tensorName == "model.norm.weight" || tensorName == "transformer.ln_f.weight")
        {
            return (ModalityLobe.TextLayernorm, "final_norm");
        }

        if (tensorName == "model.norm.bias" || tensorName == "transformer.ln_f.bias")
        {
            return (ModalityLobe.TextLayernorm, "final_norm_bias");
        }

        Match m = Gpt2PackedAttnRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextAttention, "attn_qkv_packed");
        }

        m = AttnProjRegex().Match(tensorName);
        if (m.Success)
        {
            string proj = m.Groups["proj"].Value;
            string role = proj switch
            {
                "q" => "attn_q",
                "k" => "attn_k",
                "v" => "attn_v",
                "o" => "attn_o",
                _ => string.Empty,
            };
            if (role.Length > 0)
            {
                return (ModalityLobe.TextAttention, role);
            }
        }

        m = AttnProjBiasRegex().Match(tensorName);
        if (m.Success)
        {
            string proj = m.Groups["proj"].Value;
            return (ModalityLobe.TextAttention, $"attn_{proj}_bias");
        }

        m = MlpProjRegex().Match(tensorName);
        if (m.Success)
        {
            string proj = m.Groups["proj"].Value;
            string role = proj switch
            {
                "gate" => "ffn_gate",
                "up" => "ffn_up",
                "down" => "ffn_down",
                _ => string.Empty,
            };
            if (role.Length > 0)
            {
                return (ModalityLobe.TextFfn, role);
            }
        }

        m = MlpProjBiasRegex().Match(tensorName);
        if (m.Success)
        {
            string proj = m.Groups["proj"].Value;
            return (ModalityLobe.TextFfn, $"ffn_{proj}_bias");
        }

        m = InputLayernormRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextLayernorm, "attn_norm");
        }

        m = PostAttentionLayernormRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextLayernorm, "ffn_norm");
        }

        m = InputLayernormBiasRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextLayernorm, "attn_norm_bias");
        }

        m = PostAttentionLayernormBiasRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextLayernorm, "ffn_norm_bias");
        }

        m = RotaryEmbRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextPositionRope, "rope_freq");
        }

        return null;
    }

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.(?<proj>q|k|v|o)_proj\.weight$")]
    private static partial Regex AttnProjRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.(?<proj>q|k|v|o)_proj\.bias$")]
    private static partial Regex AttnProjBiasRegex();

    [GeneratedRegex(@"^transformer\.h\.\d+\.attn\.c_attn\.weight$")]
    private static partial Regex Gpt2PackedAttnRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.(?<proj>gate|up|down)_proj\.weight$")]
    private static partial Regex MlpProjRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.(?<proj>gate|up|down)_proj\.bias$")]
    private static partial Regex MlpProjBiasRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.input_layernorm\.weight$")]
    private static partial Regex InputLayernormRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.post_attention_layernorm\.weight$")]
    private static partial Regex PostAttentionLayernormRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.input_layernorm\.bias$")]
    private static partial Regex InputLayernormBiasRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.post_attention_layernorm\.bias$")]
    private static partial Regex PostAttentionLayernormBiasRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.rotary_emb\..*$")]
    private static partial Regex RotaryEmbRegex();
}
