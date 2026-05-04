using System.Text.RegularExpressions;
using Hartonomous.Core.Operations;
using Hartonomous.Decomposers.Safetensors.Packages;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Adapters;

public sealed partial class EmbeddingAdapter : BaseArchitectureAdapter
{
    private static readonly HashSet<string> EncoderArchitectures = new(StringComparer.Ordinal)
    {
        "BertModel",
        "RobertaModel",
        "DistilBertModel",
        "XLMRobertaModel",
        "BertForSentenceEmbedding",
        "MiniLMv2",
        "JinaBertModel",
        "JinaEmbeddingModel",
        "NomicBertModel",
        "MPNetModel",
    };

    private static readonly HashSet<string> DecoderArchitecturesForEmbedding = new(StringComparer.Ordinal)
    {
        "Qwen2ForCausalLM",
        "Qwen3ForCausalLM",
        "LlamaForCausalLM",
        "MistralForCausalLM",
    };

    private static readonly string[] RequiredPaths =
    [
        "hidden_size",
        "num_attention_heads",
        "num_hidden_layers",
        "vocab_size",
    ];

    public EmbeddingAdapter(ILogger<EmbeddingAdapter> logger)
        : base(logger)
    {
    }

    public override string ArchitectureClassCode => "embedding_encoder";

    public override IReadOnlyList<string> RequiredConfigPaths => RequiredPaths;

    public override bool CanHandle(IConfigSnapshot config)
    {
        IReadOnlyList<string>? architectures = config.GetStringArray("architectures");
        if (architectures is null || architectures.Count == 0)
        {
            return false;
        }

        bool encoderMatch = false;
        bool decoderMatch = false;
        for (int i = 0; i < architectures.Count; i++)
        {
            string entry = architectures[i];
            if (EncoderArchitectures.Contains(entry))
            {
                encoderMatch = true;
                break;
            }
            if (DecoderArchitecturesForEmbedding.Contains(entry))
            {
                decoderMatch = true;
            }
        }

        if (encoderMatch)
        {
            // Encoder-class architectures are embedding models unless an explicit
            // generative head is wired (lm_head appears in tied_weights_keys or
            // architecture string). Conservative: require vocab_size present
            // (BaseArchitectureAdapter already validates RequiredConfigPaths
            // upstream, but defensive null-check here keeps CanHandle pure).
            int? vocabSize = config.GetInt32("vocab_size");
            if (vocabSize is null)
            {
                return false;
            }

            IReadOnlyList<string>? tiedKeys = config.GetStringArray("tied_weights_keys");
            if (tiedKeys is not null)
            {
                for (int i = 0; i < tiedKeys.Count; i++)
                {
                    if (tiedKeys[i].Contains("lm_head", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        if (decoderMatch)
        {
            // Qwen/Llama-class architectures used as embedders strip the LM head
            // and add a pooler. Heuristics in priority order:
            //   1) explicit task_type == "EMBEDDING"
            //   2) is_embedding_model == true
            //   3) model id / package name contains "Embedding"
            // None are present in raw HF config for some embedding repos, so a
            // conservative warn-and-claim path covers the model-card name case.
            string taskType = config.GetString("task_type", string.Empty) ?? string.Empty;
            if (string.Equals(taskType, "EMBEDDING", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool? isEmbedding = config.GetBoolean("is_embedding_model");
            if (isEmbedding == true)
            {
                return true;
            }

            string nameOrPath = config.GetString("_name_or_path", string.Empty) ?? string.Empty;
            if (nameOrPath.Length > 0 &&
                nameOrPath.Contains("Embedding", StringComparison.OrdinalIgnoreCase))
            {
                Log.AmbiguousDecoderEmbedding(Logger, nameOrPath);
                return true;
            }

            return false;
        }

        return false;
    }

    protected override (ModalityLobe Lobe, string Role)? ClassifyCore(string tensorName, int[] shape, string dtype)
    {
        if (tensorName == "embeddings.word_embeddings.weight")
        {
            return (ModalityLobe.TextEmbedding, "token_embedding");
        }

        if (tensorName == "embeddings.position_embeddings.weight")
        {
            return (ModalityLobe.TextPositionRope, "learned_position_embedding");
        }

        if (tensorName == "embeddings.token_type_embeddings.weight")
        {
            return (ModalityLobe.TextEmbedding, "token_type_embedding");
        }

        if (tensorName == "embeddings.LayerNorm.weight")
        {
            return (ModalityLobe.TextLayernorm, "embedding_norm");
        }

        if (tensorName == "embeddings.LayerNorm.bias")
        {
            return (ModalityLobe.TextLayernorm, "embedding_norm_bias");
        }

        Match m = BertAttnQRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextAttention, m.Groups["suffix"].Value == "bias" ? "attn_q_bias" : "attn_q");
        }

        m = BertAttnKRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextAttention, m.Groups["suffix"].Value == "bias" ? "attn_k_bias" : "attn_k");
        }

        m = BertAttnVRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextAttention, m.Groups["suffix"].Value == "bias" ? "attn_v_bias" : "attn_v");
        }

        m = BertAttnORegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextAttention, m.Groups["suffix"].Value == "bias" ? "attn_o_bias" : "attn_o");
        }

        m = BertAttnNormRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextLayernorm, m.Groups["suffix"].Value == "bias" ? "attn_norm_bias" : "attn_norm");
        }

        m = BertFfnIntermediateRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextFfn, m.Groups["suffix"].Value == "bias" ? "ffn_intermediate_bias" : "ffn_intermediate");
        }

        m = BertFfnOutputRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextFfn, m.Groups["suffix"].Value == "bias" ? "ffn_output_bias" : "ffn_output");
        }

        m = BertFfnNormRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextLayernorm, m.Groups["suffix"].Value == "bias" ? "ffn_norm_bias" : "ffn_norm");
        }

        if (tensorName == "pooler.dense.weight")
        {
            return (ModalityLobe.Pooler, "pooler_dense");
        }

        if (tensorName == "pooler.dense.bias")
        {
            return (ModalityLobe.Pooler, "pooler_dense_bias");
        }

        if (tensorName == "pooler.activation")
        {
            return (ModalityLobe.Pooler, "pooler_activation");
        }

        // Decoder-style fallback (Qwen2/3/Llama/Mistral repurposed as embedder).
        if (tensorName == "model.embed_tokens.weight")
        {
            return (ModalityLobe.TextEmbedding, "token_embedding");
        }

        m = DecoderAttnRegex().Match(tensorName);
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

        m = DecoderAttnBiasRegex().Match(tensorName);
        if (m.Success)
        {
            string proj = m.Groups["proj"].Value;
            return (ModalityLobe.TextAttention, $"attn_{proj}_bias");
        }

        m = DecoderMlpRegex().Match(tensorName);
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

        m = DecoderInputLayernormRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextLayernorm, "attn_norm");
        }

        m = DecoderPostAttnLayernormRegex().Match(tensorName);
        if (m.Success)
        {
            return (ModalityLobe.TextLayernorm, "ffn_norm");
        }

        if (tensorName == "model.norm.weight")
        {
            return (ModalityLobe.TextLayernorm, "final_norm");
        }

        // Embedding models should not ship an LM head. If one shows up, the
        // detection heuristic was wrong (likely a generative model misrouted
        // here). Pin it for downstream skip rather than fail the catalog.
        if (tensorName == "lm_head.weight")
        {
            Log.UnexpectedLmHead(Logger, tensorName);
            return (ModalityLobe.TextLmHead, "lm_head_unused");
        }

        return null;
    }

    [GeneratedRegex(@"^encoder\.layer\.\d+\.attention\.self\.query\.(?<suffix>weight|bias)$")]
    private static partial Regex BertAttnQRegex();

    [GeneratedRegex(@"^encoder\.layer\.\d+\.attention\.self\.key\.(?<suffix>weight|bias)$")]
    private static partial Regex BertAttnKRegex();

    [GeneratedRegex(@"^encoder\.layer\.\d+\.attention\.self\.value\.(?<suffix>weight|bias)$")]
    private static partial Regex BertAttnVRegex();

    [GeneratedRegex(@"^encoder\.layer\.\d+\.attention\.output\.dense\.(?<suffix>weight|bias)$")]
    private static partial Regex BertAttnORegex();

    [GeneratedRegex(@"^encoder\.layer\.\d+\.attention\.output\.LayerNorm\.(?<suffix>weight|bias)$")]
    private static partial Regex BertAttnNormRegex();

    [GeneratedRegex(@"^encoder\.layer\.\d+\.intermediate\.dense\.(?<suffix>weight|bias)$")]
    private static partial Regex BertFfnIntermediateRegex();

    [GeneratedRegex(@"^encoder\.layer\.\d+\.output\.dense\.(?<suffix>weight|bias)$")]
    private static partial Regex BertFfnOutputRegex();

    [GeneratedRegex(@"^encoder\.layer\.\d+\.output\.LayerNorm\.(?<suffix>weight|bias)$")]
    private static partial Regex BertFfnNormRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.(?<proj>q|k|v|o)_proj\.weight$")]
    private static partial Regex DecoderAttnRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.self_attn\.(?<proj>q|k|v|o)_proj\.bias$")]
    private static partial Regex DecoderAttnBiasRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.(?<proj>gate|up|down)_proj\.weight$")]
    private static partial Regex DecoderMlpRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.input_layernorm\.weight$")]
    private static partial Regex DecoderInputLayernormRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.post_attention_layernorm\.weight$")]
    private static partial Regex DecoderPostAttnLayernormRegex();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[embedding-adapter] decoder-style architecture matched embedding heuristic by name '{NameOrPath}' — verify task type")]
        public static partial void AmbiguousDecoderEmbedding(ILogger logger, string nameOrPath);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[embedding-adapter] encountered '{TensorName}' in embedding model — possibly mis-detected; pinned as lm_head_unused")]
        public static partial void UnexpectedLmHead(ILogger logger, string tensorName);
    }
}
