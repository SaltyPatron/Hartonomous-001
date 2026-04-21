namespace Hartonomous.Decomposers.Safetensors;

public static class TensorRoleExtensions
{
    public static string ToCode(this TensorRole role) => role switch
    {
        TensorRole.TokenEmbedding => "token_embedding",
        TensorRole.TokenTypeEmbedding => "token_type_embedding",
        TensorRole.PositionEmbedding => "position_embedding",
        TensorRole.PositionEmbedding2D => "position_embedding_2d",
        TensorRole.RopeFreq => "rope_freq",
        TensorRole.VqCodebook => "vq_codebook",
        TensorRole.ObjectQuery => "object_query",
        TensorRole.AttentionQuery => "attention_query",
        TensorRole.AttentionKey => "attention_key",
        TensorRole.AttentionValue => "attention_value",
        TensorRole.AttentionOutput => "attention_output",
        TensorRole.CrossAttention => "cross_attention",
        TensorRole.FfnGate => "ffn_gate",
        TensorRole.FfnUp => "ffn_up",
        TensorRole.FfnDown => "ffn_down",
        TensorRole.MoeRouter => "moe_router",
        TensorRole.MoeExpertGate => "moe_expert_gate",
        TensorRole.MoeExpertUp => "moe_expert_up",
        TensorRole.MoeExpertDown => "moe_expert_down",
        TensorRole.MoeSharedExpert => "moe_shared_expert",
        TensorRole.LogitHead => "logit_head",
        TensorRole.ClassHead => "class_head",
        TensorRole.BboxHead => "bbox_head",
        TensorRole.ConvKernel => "conv_kernel",
        TensorRole.DiffusionBlock => "diffusion_block",
        TensorRole.VaeBlock => "vae_block",
        TensorRole.ConformerLayer => "conformer_layer",
        TensorRole.AudioCodecEncoder => "audio_codec_encoder",
        TensorRole.AudioCodecDecoder => "audio_codec_decoder",
        TensorRole.VisionFeature => "vision_feature",
        TensorRole.VisionProjection => "vision_projection",
        TensorRole.ModalityProjection => "modality_projection",
        TensorRole.LoraA => "lora_a",
        TensorRole.LoraB => "lora_b",
        TensorRole.LayerNorm => "layer_norm",
        TensorRole.BatchNorm => "batch_norm",
        TensorRole.RmsNorm => "rms_norm",
        TensorRole.MelFilterbank => "mel_filterbank",
        TensorRole.CodebookScale => "codebook_scale",
        TensorRole.Fp8Scale => "fp8_scale",
        TensorRole.AnchorGrid => "anchor_grid",
        _ => throw new InvalidOperationException($"Cannot encode unknown tensor role {role}"),
    };

    public static bool IsTrack1(this TensorRole role) => role switch
    {
        TensorRole.TokenEmbedding or TensorRole.TokenTypeEmbedding
            or TensorRole.PositionEmbedding or TensorRole.PositionEmbedding2D
            or TensorRole.VqCodebook or TensorRole.ObjectQuery => true,
        _ => false,
    };
}
