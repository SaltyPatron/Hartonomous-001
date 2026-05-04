namespace Hartonomous.Core.Operations;

public enum ModalityLobe
{
    /// <summary>text_embedding — token embedding matrix; vocab x hidden dim.</summary>
    TextEmbedding,

    /// <summary>text_attention — self-attention Q/K/V/O projections in a text decoder layer.</summary>
    TextAttention,

    /// <summary>text_attention_cross — cross-attention projections (encoder-decoder bridge).</summary>
    TextAttentionCross,

    /// <summary>text_ffn — dense feed-forward block (gate/up/down) in a text layer.</summary>
    TextFfn,

    /// <summary>text_ffn_moe_expert — per-expert gate/up/down weights in a Mixture-of-Experts FFN.</summary>
    TextFfnMoeExpert,

    /// <summary>text_ffn_moe_router — router/gate logits selecting experts in an MoE FFN.</summary>
    TextFfnMoeRouter,

    /// <summary>text_layernorm — layer-norm / RMSNorm scale (and optional bias) tensors.</summary>
    TextLayernorm,

    /// <summary>text_position_rope — rotary position embedding cos/sin tables and inv_freq buffers.</summary>
    TextPositionRope,

    /// <summary>text_lm_head — output language-modeling head; hidden x vocab.</summary>
    TextLmHead,

    /// <summary>pooler — sentence/sequence pooling head (e.g. mean/CLS pooler dense).</summary>
    Pooler,

    /// <summary>reranker_classification_head — score head for cross-encoder rerankers.</summary>
    RerankerClassificationHead,

    /// <summary>code_specialist — code-model-specific tensors (FIM, repo embed, etc.) when distinct from generic text.</summary>
    CodeSpecialist,

    /// <summary>vision_patch_embed — image-to-patch projection (ViT/SigLIP patch embedding conv/linear).</summary>
    VisionPatchEmbed,

    /// <summary>vision_attention — self-attention projections in a vision encoder layer.</summary>
    VisionAttention,

    /// <summary>vision_ffn — feed-forward block in a vision encoder layer.</summary>
    VisionFfn,

    /// <summary>vision_layernorm — layer-norm tensors in vision encoder/decoder stacks.</summary>
    VisionLayernorm,

    /// <summary>vision_projector — vision-to-LLM projector (e.g. LLaVA mm_projector / Qwen-VL merger).</summary>
    VisionProjector,

    /// <summary>vision_class_head — image classification head (e.g. ImageNet 1000-way logits).</summary>
    VisionClassHead,

    /// <summary>vision_bbox_head — detection bounding-box regression head (DETR/Grounding-DINO).</summary>
    VisionBboxHead,

    /// <summary>vision_mask_head — segmentation mask head (Mask2Former / SAM mask decoder).</summary>
    VisionMaskHead,

    /// <summary>vision_object_queries — learnable object/query embeddings (DETR-family).</summary>
    VisionObjectQueries,

    /// <summary>vae_encoder — variational autoencoder encoder weights (image-gen latent path).</summary>
    VaeEncoder,

    /// <summary>vae_decoder — variational autoencoder decoder weights (image-gen pixel path).</summary>
    VaeDecoder,

    /// <summary>audio_spectral_frontend — STFT/mel-filterbank/preemphasis frontend buffers and convs.</summary>
    AudioSpectralFrontend,

    /// <summary>audio_codec_encoder — neural audio codec encoder (EnCodec / SoundStream / DAC).</summary>
    AudioCodecEncoder,

    /// <summary>audio_codec_decoder — neural audio codec decoder (waveform reconstruction).</summary>
    AudioCodecDecoder,

    /// <summary>audio_conformer_attn — conformer self-attention block weights.</summary>
    AudioConformerAttn,

    /// <summary>audio_conformer_conv — conformer convolution module weights.</summary>
    AudioConformerConv,

    /// <summary>audio_conformer_ffn — conformer feed-forward module weights.</summary>
    AudioConformerFfn,

    /// <summary>audio_quantizer_codebook — vector-quantizer codebook entries (RVQ levels).</summary>
    AudioQuantizerCodebook,

    /// <summary>audio_decoder_ar — autoregressive audio decoder (token-to-waveform LM head).</summary>
    AudioDecoderAr,

    /// <summary>diffusion_unet_resblock — residual blocks of a diffusion U-Net backbone.</summary>
    DiffusionUnetResblock,

    /// <summary>diffusion_unet_attn — attention blocks of a diffusion U-Net backbone.</summary>
    DiffusionUnetAttn,

    /// <summary>diffusion_timestep_embed — sinusoidal/MLP timestep embedding for diffusion conditioning.</summary>
    DiffusionTimestepEmbed,

    /// <summary>diffusion_text_encoder — text encoder feeding diffusion cross-attention (CLIP/T5).</summary>
    DiffusionTextEncoder,

    /// <summary>cross_modal_alignment — projections aligning two modalities (e.g. CLIP image/text shared head).</summary>
    CrossModalAlignment,

    /// <summary>unsupported_v1 — tensor recognized but not classified by any V1 adapter; pinned for V2/V3 work.</summary>
    UnsupportedV1,
}
