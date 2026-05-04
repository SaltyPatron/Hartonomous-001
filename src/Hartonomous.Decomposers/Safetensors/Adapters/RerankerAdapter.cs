using System.Text.RegularExpressions;
using Hartonomous.Core.Operations;
using Hartonomous.Decomposers.Safetensors.Packages;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Adapters;

public sealed partial class RerankerAdapter : BaseArchitectureAdapter
{
    private static readonly HashSet<string> SequenceClassificationArchitectures = new(StringComparer.Ordinal)
    {
        "JinaBertForSequenceClassification",
        "BertForSequenceClassification",
        "XLMRobertaForSequenceClassification",
        "Qwen2ForSequenceClassification",
        "Qwen3ForSequenceClassification",
        "RerankerModel",
        "Qwen3RerankerForRanking",
        "RobertaForSequenceClassification",
        "DistilBertForSequenceClassification",
    };

    private static readonly string[] RequiredPaths =
    [
        "hidden_size",
        "num_attention_heads",
        "num_hidden_layers",
        "vocab_size",
        "num_labels",
    ];

    public RerankerAdapter(ILogger<RerankerAdapter> logger) : base(logger)
    {
    }

    public override string ArchitectureClassCode => "reranker";

    public override IReadOnlyList<string> RequiredConfigPaths => RequiredPaths;

    public override bool CanHandle(IConfigSnapshot config)
    {
        IReadOnlyList<string>? architectures = config.GetStringArray("architectures");
        bool architectureMatch = false;
        bool isMultiLabelSeqClassifier = false;
        if (architectures is not null)
        {
            for (int i = 0; i < architectures.Count; i++)
            {
                string entry = architectures[i];
                if (SequenceClassificationArchitectures.Contains(entry))
                {
                    architectureMatch = true;
                    if (entry.EndsWith("ForSequenceClassification", StringComparison.Ordinal))
                    {
                        int? numLabels = config.GetInt32("num_labels");
                        if (numLabels is int n && n > 1)
                        {
                            isMultiLabelSeqClassifier = true;
                        }
                    }
                    break;
                }
            }
        }

        if (!architectureMatch)
        {
            string taskType = config.GetString("task_type", string.Empty) ?? string.Empty;
            bool isReranker = config.GetBoolean("is_reranker") ?? false;
            if (string.Equals(taskType, "RERANK", StringComparison.OrdinalIgnoreCase) || isReranker)
            {
                return true;
            }
            return false;
        }

        if (isMultiLabelSeqClassifier)
        {
            return false;
        }

        return true;
    }

    protected override (ModalityLobe Lobe, string Role)? ClassifyCore(string tensorName, int[] shape, string dtype)
    {
        // Reranker score-head tensors first.
        if (tensorName == "classifier.weight")
        {
            return (ModalityLobe.RerankerClassificationHead, "score_linear");
        }
        if (tensorName == "classifier.bias")
        {
            return (ModalityLobe.RerankerClassificationHead, "score_linear_bias");
        }
        if (tensorName == "classifier.dense.weight")
        {
            return (ModalityLobe.RerankerClassificationHead, "score_linear");
        }
        if (tensorName == "classifier.dense.bias")
        {
            return (ModalityLobe.RerankerClassificationHead, "score_linear_bias");
        }
        if (tensorName == "classifier.out_proj.weight")
        {
            return (ModalityLobe.RerankerClassificationHead, "score_out_proj");
        }
        if (tensorName == "classifier.out_proj.bias")
        {
            return (ModalityLobe.RerankerClassificationHead, "score_out_proj_bias");
        }
        if (tensorName == "score.weight")
        {
            return (ModalityLobe.RerankerClassificationHead, "score_linear");
        }
        if (tensorName == "score.bias")
        {
            return (ModalityLobe.RerankerClassificationHead, "score_linear_bias");
        }
        if (tensorName == "pre_classifier.weight")
        {
            return (ModalityLobe.RerankerClassificationHead, "pre_classifier");
        }
        if (tensorName == "pre_classifier.bias")
        {
            return (ModalityLobe.RerankerClassificationHead, "pre_classifier_bias");
        }

        // BERT-style embeddings.
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
            string suffix = m.Groups["suffix"].Value;
            return (ModalityLobe.TextAttention, suffix == "weight" ? "attn_q" : "attn_q_bias");
        }
        m = BertAttnKRegex().Match(tensorName);
        if (m.Success)
        {
            string suffix = m.Groups["suffix"].Value;
            return (ModalityLobe.TextAttention, suffix == "weight" ? "attn_k" : "attn_k_bias");
        }
        m = BertAttnVRegex().Match(tensorName);
        if (m.Success)
        {
            string suffix = m.Groups["suffix"].Value;
            return (ModalityLobe.TextAttention, suffix == "weight" ? "attn_v" : "attn_v_bias");
        }
        m = BertAttnORegex().Match(tensorName);
        if (m.Success)
        {
            string suffix = m.Groups["suffix"].Value;
            return (ModalityLobe.TextAttention, suffix == "weight" ? "attn_o" : "attn_o_bias");
        }
        m = BertAttnNormRegex().Match(tensorName);
        if (m.Success)
        {
            string suffix = m.Groups["suffix"].Value;
            return (ModalityLobe.TextLayernorm, suffix == "weight" ? "attn_norm" : "attn_norm_bias");
        }
        m = BertFfnIntermediateRegex().Match(tensorName);
        if (m.Success)
        {
            string suffix = m.Groups["suffix"].Value;
            return (ModalityLobe.TextFfn, suffix == "weight" ? "ffn_intermediate" : "ffn_intermediate_bias");
        }
        m = BertFfnOutputRegex().Match(tensorName);
        if (m.Success)
        {
            string suffix = m.Groups["suffix"].Value;
            return (ModalityLobe.TextFfn, suffix == "weight" ? "ffn_output" : "ffn_output_bias");
        }
        m = BertFfnNormRegex().Match(tensorName);
        if (m.Success)
        {
            string suffix = m.Groups["suffix"].Value;
            return (ModalityLobe.TextLayernorm, suffix == "weight" ? "ffn_norm" : "ffn_norm_bias");
        }

        if (tensorName == "pooler.dense.weight")
        {
            return (ModalityLobe.Pooler, "pooler_dense");
        }
        if (tensorName == "pooler.dense.bias")
        {
            return (ModalityLobe.Pooler, "pooler_dense_bias");
        }

        // Decoder-style fallback for Qwen-Reranker.
        if (tensorName == "model.embed_tokens.weight")
        {
            return (ModalityLobe.TextEmbedding, "token_embedding");
        }
        m = QwenAttnProjRegex().Match(tensorName);
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
        m = QwenMlpProjRegex().Match(tensorName);
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
        if (QwenInputLayernormRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextLayernorm, "attn_norm");
        }
        if (QwenPostAttentionLayernormRegex().IsMatch(tensorName))
        {
            return (ModalityLobe.TextLayernorm, "ffn_norm");
        }
        if (tensorName == "model.norm.weight")
        {
            return (ModalityLobe.TextLayernorm, "final_norm");
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
    private static partial Regex QwenAttnProjRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.mlp\.(?<proj>gate|up|down)_proj\.weight$")]
    private static partial Regex QwenMlpProjRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.input_layernorm\.weight$")]
    private static partial Regex QwenInputLayernormRegex();

    [GeneratedRegex(@"^model\.layers\.\d+\.post_attention_layernorm\.weight$")]
    private static partial Regex QwenPostAttentionLayernormRegex();
}
