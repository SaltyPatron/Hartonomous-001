using System.Globalization;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors;

public sealed record TensorClassification(
    TensorRole Role,
    int? LayerIndex,
    int? ExpertIndex);

public static partial class TensorClassifier
{
    public static TensorClassification Classify(string tensorName, string architectureClass)
    {
        // MoE expert patterns must run first (more specific than plain mlp).
        Match m;

        // Qwen MoE / DeepseekMoE expert patterns.
        m = MoeExpertRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            int expert = int.Parse(m.Groups["expert"].Value, CultureInfo.InvariantCulture);
            TensorRole role = m.Groups["proj"].Value switch
            {
                "gate_proj" => TensorRole.MoeExpertGate,
                "up_proj" => TensorRole.MoeExpertUp,
                "down_proj" => TensorRole.MoeExpertDown,
                _ => TensorRole.Unknown,
            };
            return new TensorClassification(role, layer, expert);
        }

        m = MoeSharedRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.MoeSharedExpert, layer, null);
        }

        m = MoeRouterRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.MoeRouter, layer, null);
        }

        // Llama/Qwen/Mistral decoder: model.layers.N.self_attn.{q,k,v,o}_proj + mlp.{gate,up,down}_proj.
        m = DecoderAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            TensorRole role = m.Groups["proj"].Value switch
            {
                "q_proj" => TensorRole.AttentionQuery,
                "k_proj" => TensorRole.AttentionKey,
                "v_proj" => TensorRole.AttentionValue,
                "o_proj" => TensorRole.AttentionOutput,
                _ => TensorRole.Unknown,
            };
            return new TensorClassification(role, layer, null);
        }

        m = DecoderMlpRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            TensorRole role = m.Groups["proj"].Value switch
            {
                "gate_proj" => TensorRole.FfnGate,
                "up_proj" => TensorRole.FfnUp,
                "down_proj" => TensorRole.FfnDown,
                _ => TensorRole.Unknown,
            };
            return new TensorClassification(role, layer, null);
        }

        m = DecoderLayerNormRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.RmsNorm, layer, null);
        }

        // BERT encoder: bert.encoder.layer.N.attention.self.{query,key,value}
        m = BertAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            TensorRole role = m.Groups["proj"].Value switch
            {
                "query" => TensorRole.AttentionQuery,
                "key" => TensorRole.AttentionKey,
                "value" => TensorRole.AttentionValue,
                _ => TensorRole.Unknown,
            };
            return new TensorClassification(role, layer, null);
        }

        m = BertAttnOutputRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.AttentionOutput, layer, null);
        }

        m = BertIntermediateRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.FfnUp, layer, null);
        }

        m = BertFfnDownRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.FfnDown, layer, null);
        }

        m = BertLayerNormRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.LayerNorm, layer, null);
        }

        // Embeddings (Track 1).
        if (tensorName.EndsWith("embeddings.word_embeddings.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("embed_tokens.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("tok_embeddings.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("word_embeddings.weight", StringComparison.Ordinal) ||
            tensorName == "model.embed_tokens.weight" ||
            tensorName == "tok_embeddings.weight")
        {
            return new TensorClassification(TensorRole.TokenEmbedding, null, null);
        }

        if (tensorName.EndsWith("embeddings.token_type_embeddings.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("token_type_embeddings.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.TokenTypeEmbedding, null, null);
        }

        if (tensorName.EndsWith("embeddings.position_embeddings.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("position_embeddings.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("wpe.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.PositionEmbedding, null, null);
        }

        // Embeddings LayerNorm (BERT) — reference only.
        if (tensorName.StartsWith("bert.embeddings.LayerNorm", StringComparison.Ordinal) ||
            tensorName.EndsWith("embeddings.LayerNorm.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("embeddings.LayerNorm.bias", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.LayerNorm, null, null);
        }

        // Final norm (RmsNorm for Llama, LayerNorm for BERT pooler).
        if (tensorName == "model.norm.weight" || tensorName == "norm.weight")
        {
            return new TensorClassification(TensorRole.RmsNorm, null, null);
        }

        // LM head / logit projection.
        if (tensorName == "lm_head.weight" ||
            tensorName == "output.weight" ||
            tensorName == "embed_out.weight")
        {
            return new TensorClassification(TensorRole.LogitHead, null, null);
        }

        // DETR object queries and heads.
        if (tensorName.Contains("query_position_embeddings", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.ObjectQuery, null, null);
        }
        if (tensorName.Contains("class_labels_classifier", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.ClassHead, null, null);
        }
        if (tensorName.Contains("bbox_predictor", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.BboxHead, null, null);
        }

        // RoPE / rotary frequency buffers.
        if (tensorName.EndsWith("rotary_emb.inv_freq", StringComparison.Ordinal) ||
            tensorName.EndsWith("rope.freqs", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.RopeFreq, null, null);
        }

        // LoRA adapters.
        if (tensorName.Contains("lora_A", StringComparison.Ordinal) ||
            tensorName.EndsWith(".lora_A.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.LoraA, null, null);
        }
        if (tensorName.Contains("lora_B", StringComparison.Ordinal) ||
            tensorName.EndsWith(".lora_B.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.LoraB, null, null);
        }

        return new TensorClassification(TensorRole.Unknown, null, null);
    }

    [GeneratedRegex(@"layers\.(?<layer>\d+)\.mlp\.experts\.(?<expert>\d+)\.(?<proj>gate_proj|up_proj|down_proj)\.weight$")]
    private static partial Regex MoeExpertRegex();

    [GeneratedRegex(@"layers\.(?<layer>\d+)\.mlp\.shared_experts?\.")]
    private static partial Regex MoeSharedRegex();

    [GeneratedRegex(@"layers\.(?<layer>\d+)\.mlp\.(?:gate|router)\.weight$")]
    private static partial Regex MoeRouterRegex();

    [GeneratedRegex(@"layers\.(?<layer>\d+)\.self_attn\.(?<proj>q_proj|k_proj|v_proj|o_proj)\.weight$")]
    private static partial Regex DecoderAttnRegex();

    [GeneratedRegex(@"layers\.(?<layer>\d+)\.mlp\.(?<proj>gate_proj|up_proj|down_proj)\.weight$")]
    private static partial Regex DecoderMlpRegex();

    [GeneratedRegex(@"layers\.(?<layer>\d+)\.(?:input_layernorm|post_attention_layernorm)\.weight$")]
    private static partial Regex DecoderLayerNormRegex();

    [GeneratedRegex(@"encoder\.layer\.(?<layer>\d+)\.attention\.self\.(?<proj>query|key|value)\.")]
    private static partial Regex BertAttnRegex();

    [GeneratedRegex(@"encoder\.layer\.(?<layer>\d+)\.attention\.output\.dense\.")]
    private static partial Regex BertAttnOutputRegex();

    [GeneratedRegex(@"encoder\.layer\.(?<layer>\d+)\.intermediate\.dense\.")]
    private static partial Regex BertIntermediateRegex();

    [GeneratedRegex(@"encoder\.layer\.(?<layer>\d+)\.output\.dense\.")]
    private static partial Regex BertFfnDownRegex();

    [GeneratedRegex(@"encoder\.layer\.(?<layer>\d+)\.(?:attention\.output\.LayerNorm|output\.LayerNorm)\.")]
    private static partial Regex BertLayerNormRegex();
}
