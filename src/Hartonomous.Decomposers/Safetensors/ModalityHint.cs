namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// What content-entity-type this tuple's attestations bind to. Per
/// docs/01-tensor-primitive-spec.md §I. Determines which substrate
/// content-entity (word_form, pixel_region, audio_chunk, etc.) the
/// attestation edges land on. CrossAttention tuples carry TWO modality
/// hints (one per stream); single-attention tuples carry one.
/// </summary>
public enum ModalityHint
{
    Unknown = 0,
    /// <summary>Token-level text content. Binds attestations to word_form entities.</summary>
    Text,
    /// <summary>Encoder-side text (BART encoder). Distinguished from decoder text for cross-attn binding.</summary>
    TextEncoder,
    /// <summary>Decoder-side text (BART decoder). Distinguished from encoder text for cross-attn binding.</summary>
    TextDecoder,
    /// <summary>Image-patch content. Binds attestations to pixel_region entities.</summary>
    ImagePatch,
    /// <summary>Audio-frame content. Binds attestations to audio_chunk entities.</summary>
    AudioFrame,
    /// <summary>VQ-VAE / EnCodec / RVQ codeword content. Binds attestations to codec_codevector entities.</summary>
    CodecCodeword,
    /// <summary>Cross-modal binding entity (CLIP / Florence-2 image-aligned text concept). Binds to visual_concept entities.</summary>
    VisualConcept,
    /// <summary>DETR / Grounding-DINO learned object query slot.</summary>
    ObjectQuery,
    /// <summary>Position embedding entity — position-as-content.</summary>
    Position,
}
