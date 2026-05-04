using System.Text.RegularExpressions;
using Hartonomous.Core.Operations;
using Hartonomous.Decomposers.Safetensors.Packages;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Adapters;

public sealed partial class MoeAdapter : BaseArchitectureAdapter
{
    private static readonly HashSet<string> SupportedArchitectureClasses = new(StringComparer.Ordinal)
    {
        "Qwen3MoeForCausalLM",
        "Qwen3_MoeForCausalLM",
        "MixtralForCausalLM",
        "DeepseekV2ForCausalLM",
        "DeepseekV3ForCausalLM",
        "DeepseekV32ForCausalLM",
        "Llama4ForConditionalGeneration",
    };

    private static readonly HashSet<string> SupportedModelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "qwen3_moe",
        "mixtral",
        "deepseek_v2",
        "deepseek_v3",
        "deepseek_v32",
    };

    private static readonly string[] ExpertCountConfigPaths =
    [
        "num_experts",
        "num_local_experts",
        "n_routed_experts",
    ];

    public MoeAdapter(ILogger<MoeAdapter> logger) : base(logger)
    {
    }

    public override string ArchitectureClassCode => "moe_llm";

    public override IReadOnlyList<string> RequiredConfigPaths =>
    [
        "hidden_size",
        "num_attention_heads",
        "num_hidden_layers",
        "vocab_size",
        "intermediate_size",
    ];

    public override bool CanHandle(IConfigSnapshot config)
    {
        int expertCount = ReadExpertCount(config);

        IReadOnlyList<string>? architectures = config.GetStringArray("architectures");
        if (architectures is not null)
        {
            foreach (string arch in architectures)
            {
                if (!SupportedArchitectureClasses.Contains(arch))
                {
                    continue;
                }

                if (arch == "DeepseekV2ForCausalLM")
                {
                    if (expertCount > 0)
                    {
                        return true;
                    }
                    continue;
                }

                if (arch == "Llama4ForConditionalGeneration")
                {
                    if (expertCount > 0)
                    {
                        return true;
                    }
                    continue;
                }

                return true;
            }
        }

        string modelType = config.GetString("model_type", string.Empty);
        if (!string.IsNullOrEmpty(modelType) && SupportedModelTypes.Contains(modelType))
        {
            return true;
        }

        return expertCount > 0;
    }

    private static int ReadExpertCount(IConfigSnapshot config)
    {
        foreach (string path in ExpertCountConfigPaths)
        {
            int? value = config.GetInt32(path);
            if (value.HasValue && value.Value > 0)
            {
                return value.Value;
            }
        }
        return 0;
    }

    protected override (ModalityLobe Lobe, string Role)? ClassifyCore(string tensorName, int[] shape, string dtype)
    {
        if (MoeExpertGateRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeExpert, "moe_expert_gate");
        }
        if (MoeExpertUpRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeExpert, "moe_expert_up");
        }
        if (MoeExpertDownRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeExpert, "moe_expert_down");
        }
        if (MixtralExpertW1Regex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeExpert, "moe_expert_w1");
        }
        if (MixtralExpertW2Regex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeExpert, "moe_expert_w2");
        }
        if (MixtralExpertW3Regex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeExpert, "moe_expert_w3");
        }
        if (Qwen3MoeRouterRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeRouter, "moe_router");
        }
        if (MixtralRouterRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeRouter, "moe_router");
        }
        if (SharedExpertGateRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfn, "shared_expert_gate");
        }
        if (SharedExpertUpRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfn, "shared_expert_up");
        }
        if (SharedExpertDownRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfn, "shared_expert_down");
        }
        if (SharedExpertGateLogitRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextFfnMoeRouter, "shared_expert_gate_logit");
        }
        if (KvIndexerRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "kv_indexer");
        }
        if (KvALayernormRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextLayernorm, "kv_a_layernorm");
        }
        if (MlaKvAProjRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "mla_kv_a_proj");
        }
        if (MlaKvBProjRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "mla_kv_b_proj");
        }
        if (MlaQAProjRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "mla_q_a_proj");
        }
        if (MlaQBProjRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "mla_q_b_proj");
        }
        if (QALayernormRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextLayernorm, "q_a_layernorm");
        }

        if (tensorName == "model.embed_tokens.weight")
        {
            return (ModalityLobe.TextEmbedding, "token_embedding");
        }
        if (tensorName == "lm_head.weight")
        {
            return (ModalityLobe.TextLmHead, "lm_head");
        }
        if (AttnQRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "attn_q");
        }
        if (AttnKRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "attn_k");
        }
        if (AttnVRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "attn_v");
        }
        if (AttnORegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextAttention, "attn_o");
        }
        if (InputLayernormRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextLayernorm, "attn_norm");
        }
        if (PostAttentionLayernormRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextLayernorm, "ffn_norm");
        }
        if (tensorName == "model.norm.weight")
        {
            return (ModalityLobe.TextLayernorm, "final_norm");
        }

        return null;
    }

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.experts\.\d+\.gate_proj\.weight$")]
    private static partial Regex MoeExpertGateRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.experts\.\d+\.up_proj\.weight$")]
    private static partial Regex MoeExpertUpRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.experts\.\d+\.down_proj\.weight$")]
    private static partial Regex MoeExpertDownRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.block_sparse_moe\.experts\.\d+\.w1\.weight$")]
    private static partial Regex MixtralExpertW1Regex();

    [GeneratedRegex(@"^model\.layers\.\d+\.block_sparse_moe\.experts\.\d+\.w2\.weight$")]
    private static partial Regex MixtralExpertW2Regex();

    [GeneratedRegex(@"^model\.layers\.\d+\.block_sparse_moe\.experts\.\d+\.w3\.weight$")]
    private static partial Regex MixtralExpertW3Regex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.gate\.weight$")]
    private static partial Regex Qwen3MoeRouterRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.block_sparse_moe\.gate\.weight$")]
    private static partial Regex MixtralRouterRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.shared_expert\.gate_proj\.weight$")]
    private static partial Regex SharedExpertGateRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.shared_expert\.up_proj\.weight$")]
    private static partial Regex SharedExpertUpRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.shared_expert\.down_proj\.weight$")]
    private static partial Regex SharedExpertDownRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.shared_expert_gate\.weight$")]
    private static partial Regex SharedExpertGateLogitRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.kv_indexer\..*")]
    private static partial Regex KvIndexerRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.kv_a_layernorm\.weight$")]
    private static partial Regex KvALayernormRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.kv_a_proj_with_mqa\.weight$")]
    private static partial Regex MlaKvAProjRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.kv_b_proj\.weight$")]
    private static partial Regex MlaKvBProjRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.q_a_proj\.weight$")]
    private static partial Regex MlaQAProjRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.q_b_proj\.weight$")]
    private static partial Regex MlaQBProjRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.q_a_layernorm\.weight$")]
    private static partial Regex QALayernormRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.q_proj\.weight$")]
    private static partial Regex AttnQRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.k_proj\.weight$")]
    private static partial Regex AttnKRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.v_proj\.weight$")]
    private static partial Regex AttnVRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.o_proj\.weight$")]
    private static partial Regex AttnORegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.input_layernorm\.weight$")]
    private static partial Regex InputLayernormRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.post_attention_layernorm\.weight$")]
    private static partial Regex PostAttentionLayernormRegex();
}
