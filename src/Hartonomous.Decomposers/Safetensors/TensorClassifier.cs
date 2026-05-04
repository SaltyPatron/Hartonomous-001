using System.Globalization;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors;

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

        // === DETR-family encoder/decoder (Conditional-DETR, RT-DETR, Grounding-DINO) ===
        m = DetrEncoderAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(MapDetrAttn(m.Groups["proj"].Value), layer, null);
        }
        m = DetrEncoderFfnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(
                m.Groups["fc"].Value == "fc1" ? TensorRole.FfnUp : TensorRole.FfnDown,
                layer, null);
        }
        m = DetrEncoderNormRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.LayerNorm, layer, null);
        }
        m = DetrDecoderAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            string attn = m.Groups["attn"].Value;
            string proj = m.Groups["proj"].Value;
            TensorRole role = attn == "encoder_attn"
                ? TensorRole.CrossAttention
                : MapDetrAttn(proj);
            return new TensorClassification(role, layer, null);
        }
        m = DetrDecoderFfnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(
                m.Groups["fc"].Value == "fc1" ? TensorRole.FfnUp : TensorRole.FfnDown,
                layer, null);
        }
        m = DetrDecoderNormRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(TensorRole.LayerNorm, layer, null);
        }
        if (tensorName.EndsWith("model.input_projection.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("input_projection.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.VisionProjection, null, null);
        }
        if (tensorName.EndsWith("query_embed.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.ObjectQuery, null, null);
        }
        // Backbone convolutions (ResNet-50/101 in DETR; Swin in Grounding-DINO conv stem).
        m = ResnetConvRegex().Match(tensorName);
        if (m.Success)
        {
            return new TensorClassification(TensorRole.ConvKernel, null, null);
        }
        if (tensorName.Contains(".batch_norm.", StringComparison.Ordinal) ||
            tensorName.EndsWith(".bn.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith(".bn.bias", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.BatchNorm, null, null);
        }

        // === Florence-2 DaVit vision tower ===
        m = DavitAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int stage = int.Parse(m.Groups["stage"].Value, CultureInfo.InvariantCulture);
            string proj = m.Groups["proj"].Value;
            TensorRole role = proj == "qkv" ? TensorRole.AttentionQuery : TensorRole.AttentionOutput;
            return new TensorClassification(role, stage, null);
        }
        m = DavitMlpRegex().Match(tensorName);
        if (m.Success)
        {
            int stage = int.Parse(m.Groups["stage"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(
                m.Groups["fc"].Value == "fc1" ? TensorRole.FfnUp : TensorRole.FfnDown,
                stage, null);
        }
        if (tensorName.EndsWith("image_projection.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("image_proj_norm.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("visual_projection.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.VisionProjection, null, null);
        }

        // === BART-style text decoder (Florence-2 language model, T5/MBart variants) ===
        m = BartDecoderAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            string attn = m.Groups["attn"].Value;
            TensorRole role = attn == "encoder_attn"
                ? TensorRole.CrossAttention
                : MapDetrAttn(m.Groups["proj"].Value);
            return new TensorClassification(role, layer, null);
        }
        m = BartDecoderFfnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(
                m.Groups["fc"].Value == "fc1" ? TensorRole.FfnUp : TensorRole.FfnDown,
                layer, null);
        }

        // === FLUX diffusion transformer (Black Forest Labs FLUX, SD3 transformers) ===
        m = FluxAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int block = int.Parse(m.Groups["block"].Value, CultureInfo.InvariantCulture);
            TensorRole role = m.Groups["proj"].Value switch
            {
                "to_q" or "add_q_proj" => TensorRole.AttentionQuery,
                "to_k" or "add_k_proj" => TensorRole.AttentionKey,
                "to_v" or "add_v_proj" => TensorRole.AttentionValue,
                "to_out.0" or "to_add_out" => TensorRole.AttentionOutput,
                _ => TensorRole.Unknown,
            };
            // Cross-attention add_*_proj projections inject text-encoder context.
            if (m.Groups["proj"].Value.StartsWith("add_", StringComparison.Ordinal))
            {
                role = TensorRole.CrossAttention;
            }
            return new TensorClassification(role, block, null);
        }
        m = FluxFfnRegex().Match(tensorName);
        if (m.Success)
        {
            int block = int.Parse(m.Groups["block"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(
                m.Groups["pos"].Value == "0" ? TensorRole.FfnUp : TensorRole.FfnDown,
                block, null);
        }
        if (FluxBlockCatchallRegex().IsMatch(tensorName))
        {
            return new TensorClassification(TensorRole.DiffusionBlock, null, null);
        }
        if (tensorName.EndsWith("pos_embed.proj.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("pos_embed.pos_embed", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.PositionEmbedding2D, null, null);
        }
        if (tensorName.EndsWith("x_embedder.proj.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("x_embedder.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.VisionProjection, null, null);
        }
        if (tensorName.EndsWith("context_embedder.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.ModalityProjection, null, null);
        }
        if (tensorName.Contains("time_text_embed", StringComparison.Ordinal) ||
            tensorName.Contains("t_embedder", StringComparison.Ordinal) ||
            tensorName.EndsWith("timestep_embed.linear_1.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("timestep_embed.linear_2.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.DiffusionBlock, null, null);
        }

        // === VAE (FLUX vae, SD VAE, AutoencoderKL) ===
        if (VaeBlockRegex().IsMatch(tensorName))
        {
            return new TensorClassification(TensorRole.VaeBlock, null, null);
        }
        if (tensorName.EndsWith("encoder.conv_in.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("encoder.conv_out.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("decoder.conv_in.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("decoder.conv_out.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("quant_conv.weight", StringComparison.Ordinal) ||
            tensorName.EndsWith("post_quant_conv.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.ConvKernel, null, null);
        }

        // === T5 text encoder (FLUX text_encoder_2, generic T5) ===
        m = T5AttnRegex().Match(tensorName);
        if (m.Success)
        {
            int block = int.Parse(m.Groups["block"].Value, CultureInfo.InvariantCulture);
            TensorRole role = m.Groups["proj"].Value switch
            {
                "q" => TensorRole.AttentionQuery,
                "k" => TensorRole.AttentionKey,
                "v" => TensorRole.AttentionValue,
                "o" => TensorRole.AttentionOutput,
                _ => TensorRole.Unknown,
            };
            return new TensorClassification(role, block, null);
        }
        m = T5FfnRegex().Match(tensorName);
        if (m.Success)
        {
            int block = int.Parse(m.Groups["block"].Value, CultureInfo.InvariantCulture);
            string proj = m.Groups["proj"].Value;
            TensorRole role = proj.StartsWith("wi", StringComparison.Ordinal)
                ? (proj == "wi_0" ? TensorRole.FfnGate : TensorRole.FfnUp)
                : TensorRole.FfnDown;
            return new TensorClassification(role, block, null);
        }
        if (tensorName.EndsWith("shared.weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.TokenEmbedding, null, null);
        }

        // === CLIP text encoder (FLUX text_encoder, generic CLIP) ===
        m = ClipTextAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            TensorRole role = m.Groups["proj"].Value switch
            {
                "q_proj" => TensorRole.AttentionQuery,
                "k_proj" => TensorRole.AttentionKey,
                "v_proj" => TensorRole.AttentionValue,
                "out_proj" => TensorRole.AttentionOutput,
                _ => TensorRole.Unknown,
            };
            return new TensorClassification(role, layer, null);
        }
        m = ClipTextFfnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(
                m.Groups["fc"].Value == "fc1" ? TensorRole.FfnUp : TensorRole.FfnDown,
                layer, null);
        }

        // === Conformer encoder (Granite-Speech, NVIDIA Canary, SAM-Audio) ===
        m = ConformerAttnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            TensorRole role = m.Groups["proj"].Value switch
            {
                "linear_q" or "q_proj" => TensorRole.AttentionQuery,
                "linear_k" or "k_proj" => TensorRole.AttentionKey,
                "linear_v" or "v_proj" => TensorRole.AttentionValue,
                "linear_out" or "out_proj" => TensorRole.AttentionOutput,
                _ => TensorRole.Unknown,
            };
            return new TensorClassification(role, layer, null);
        }
        if (ConformerConvRegex().IsMatch(tensorName))
        {
            return new TensorClassification(TensorRole.ConformerLayer, null, null);
        }
        m = ConformerFfnRegex().Match(tensorName);
        if (m.Success)
        {
            int layer = int.Parse(m.Groups["layer"].Value, CultureInfo.InvariantCulture);
            return new TensorClassification(
                m.Groups["pos"].Value == "1" ? TensorRole.FfnUp : TensorRole.FfnDown,
                layer, null);
        }

        // === Audio codec / VQ (fish-speech, SoundStream-style codecs) ===
        if (tensorName.Contains("codebook", StringComparison.OrdinalIgnoreCase) ||
            tensorName.Contains("quantizer", StringComparison.OrdinalIgnoreCase) ||
            tensorName.EndsWith(".vq.embed", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.VqCodebook, null, null);
        }
        if (tensorName.StartsWith("encoder.", StringComparison.Ordinal) &&
            tensorName.EndsWith(".weight", StringComparison.Ordinal))
        {
            // Heuristic — only fires if we got here without classifying. Audio
            // codec encoders share the encoder.* prefix but no LLM patterns matched.
            return new TensorClassification(TensorRole.AudioCodecEncoder, null, null);
        }
        if (tensorName.StartsWith("decoder.", StringComparison.Ordinal) &&
            tensorName.EndsWith(".weight", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.AudioCodecDecoder, null, null);
        }

        // FP8 dynamic-quantization scale tensors (DeepSeek-V3.2, Llama-4-Maverick).
        if (tensorName.EndsWith(".weight_scale_inv", StringComparison.Ordinal) ||
            tensorName.EndsWith(".scale_inv", StringComparison.Ordinal) ||
            tensorName.EndsWith(".weight_scale", StringComparison.Ordinal))
        {
            return new TensorClassification(TensorRole.Fp8Scale, null, null);
        }

        // YOLO anchor grids (when .pt parses as state dict).
        if (tensorName.Contains("anchor_grid", StringComparison.Ordinal) ||
            tensorName.Contains("anchors", StringComparison.OrdinalIgnoreCase))
        {
            return new TensorClassification(TensorRole.AnchorGrid, null, null);
        }

        return new TensorClassification(TensorRole.Unknown, null, null);
    }

    private static TensorRole MapDetrAttn(string proj) => proj switch
    {
        "q_proj" or "query_proj" or "linear_q" => TensorRole.AttentionQuery,
        "k_proj" or "key_proj" or "linear_k" => TensorRole.AttentionKey,
        "v_proj" or "value_proj" or "linear_v" => TensorRole.AttentionValue,
        "out_proj" or "output_proj" or "linear_out" => TensorRole.AttentionOutput,
        _ => TensorRole.Unknown,
    };

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

    // === DETR-family regexes ===

    [GeneratedRegex(@"(?:^|\.)encoder\.layers\.(?<layer>\d+)\.self_attn\.(?<proj>q_proj|k_proj|v_proj|out_proj)\.")]
    private static partial Regex DetrEncoderAttnRegex();

    [GeneratedRegex(@"(?:^|\.)encoder\.layers\.(?<layer>\d+)\.(?<fc>fc1|fc2)\.")]
    private static partial Regex DetrEncoderFfnRegex();

    [GeneratedRegex(@"(?:^|\.)encoder\.layers\.(?<layer>\d+)\.(?:self_attn_layer_norm|final_layer_norm)\.")]
    private static partial Regex DetrEncoderNormRegex();

    [GeneratedRegex(@"(?:^|\.)decoder\.layers\.(?<layer>\d+)\.(?<attn>self_attn|encoder_attn|cross_attn)\.(?<proj>q_proj|k_proj|v_proj|out_proj)\.")]
    private static partial Regex DetrDecoderAttnRegex();

    [GeneratedRegex(@"(?:^|\.)decoder\.layers\.(?<layer>\d+)\.(?<fc>fc1|fc2)\.")]
    private static partial Regex DetrDecoderFfnRegex();

    [GeneratedRegex(@"(?:^|\.)decoder\.layers\.(?<layer>\d+)\.(?:self_attn_layer_norm|encoder_attn_layer_norm|cross_attn_layer_norm|final_layer_norm)\.")]
    private static partial Regex DetrDecoderNormRegex();

    [GeneratedRegex(@"(?:^|\.)backbone\.(?:conv_encoder\.model\.)?(?:layer\d+\.\d+\.|conv1\.|stem\.)(?:conv\d*|downsample\.0)\.weight$")]
    private static partial Regex ResnetConvRegex();

    // === Florence-2 DaVit vision tower regexes ===

    [GeneratedRegex(@"davit_stages\.(?<stage>\d+)\.blocks\.\d+\.attention\.(?<proj>qkv|proj)\.weight$")]
    private static partial Regex DavitAttnRegex();

    [GeneratedRegex(@"davit_stages\.(?<stage>\d+)\.blocks\.\d+\.mlp\.(?<fc>fc1|fc2)\.weight$")]
    private static partial Regex DavitMlpRegex();

    // === BART-style text decoder (Florence-2 language_model, T5-encoder, MBart) ===

    [GeneratedRegex(@"language_model\.(?:model\.)?decoder\.layers\.(?<layer>\d+)\.(?<attn>self_attn|encoder_attn)\.(?<proj>q_proj|k_proj|v_proj|out_proj)\.")]
    private static partial Regex BartDecoderAttnRegex();

    [GeneratedRegex(@"language_model\.(?:model\.)?decoder\.layers\.(?<layer>\d+)\.(?<fc>fc1|fc2)\.")]
    private static partial Regex BartDecoderFfnRegex();

    // === FLUX diffusion transformer regexes ===

    [GeneratedRegex(@"transformer_blocks\.(?<block>\d+)\.attn\.(?<proj>to_q|to_k|to_v|to_out\.0|add_q_proj|add_k_proj|add_v_proj|to_add_out)\.weight$")]
    private static partial Regex FluxAttnRegex();

    [GeneratedRegex(@"transformer_blocks\.(?<block>\d+)\.ff(?:_context)?\.net\.(?<pos>0|2)\.proj\.weight$")]
    private static partial Regex FluxFfnRegex();

    [GeneratedRegex(@"transformer_blocks\.\d+\.(?:norm\d*(?:_context)?|attn\.norm_(?:added_)?[qk]|ff\.|ff_context\.)")]
    private static partial Regex FluxBlockCatchallRegex();

    // === VAE (FLUX vae, SD VAE, AutoencoderKL) ===

    [GeneratedRegex(@"(?:^|\.)(?:vae|first_stage_model)\.(?:encoder|decoder)\.(?:down_blocks|up_blocks|mid_block)\.")]
    private static partial Regex VaeBlockRegex();

    // === T5 text encoder (FLUX text_encoder_2, generic T5) ===

    [GeneratedRegex(@"encoder\.block\.(?<block>\d+)\.layer\.\d+\.SelfAttention\.(?<proj>q|k|v|o)\.weight$")]
    private static partial Regex T5AttnRegex();

    [GeneratedRegex(@"encoder\.block\.(?<block>\d+)\.layer\.\d+\.DenseReluDense\.(?<proj>wi|wi_0|wi_1|wo)\.weight$")]
    private static partial Regex T5FfnRegex();

    // === CLIP text encoder (FLUX text_encoder, generic CLIP) ===

    [GeneratedRegex(@"text_model\.encoder\.layers\.(?<layer>\d+)\.self_attn\.(?<proj>q_proj|k_proj|v_proj|out_proj)\.weight$")]
    private static partial Regex ClipTextAttnRegex();

    [GeneratedRegex(@"text_model\.encoder\.layers\.(?<layer>\d+)\.mlp\.(?<fc>fc1|fc2)\.weight$")]
    private static partial Regex ClipTextFfnRegex();

    // === Conformer encoder (Granite-Speech, NVIDIA Canary, SAM-Audio) ===

    [GeneratedRegex(@"(?:^|\.)(?:encoder|conformer(?:_encoder)?)\.layers?\.(?<layer>\d+)\.self_attn\.(?<proj>linear_q|linear_k|linear_v|linear_out|q_proj|k_proj|v_proj|out_proj)\.")]
    private static partial Regex ConformerAttnRegex();

    [GeneratedRegex(@"(?:^|\.)(?:encoder|conformer(?:_encoder)?)\.layers?\.\d+\.conv_module\.(?:pointwise_conv1|pointwise_conv2|depthwise_conv|norm)\.")]
    private static partial Regex ConformerConvRegex();

    [GeneratedRegex(@"(?:^|\.)(?:encoder|conformer(?:_encoder)?)\.layers?\.(?<layer>\d+)\.(?:feed_forward|ff)(?:_macaron)?\.linear(?<pos>1|2)\.")]
    private static partial Regex ConformerFfnRegex();
}
