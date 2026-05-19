# Decomposer architecture (layer-type factoring)

Source: `docs/00-substrate-spec.md` §V, `docs/01-tensor-primitive-spec.md`, AP-26 / AP-30 / AP-32, `docs/specs/decomposers/layer-type-library.md`.

## Factoring by tensor layer-type, NOT by downstream modality

A vision transformer's patch attention is the same math as a text encoder's token attention; only the content entities the attestations bind change. A diffusion transformer (DiT) in Flux uses the same self-attention math as a Llama; only the cross-attention to image latents differs. Once you have a library of layer-type decomposers, ingesting a new model is composition, not bespoke code.

Modality is a downstream USE property — what a model is for in product terms. Layer-type is what the tensor math actually IS. Layer-type decomposers are universal across architectures that use them.

## The standardization — 4 primitives + ~13 tuples + per-architecture TupleResolver

| Vocabulary axis | Values |
|---|---|
| **PrimitiveKind** (4) | `Linear`, `LocalKernel`, `Normalization`, `Lookup` |
| **ArchetypeTuple** (~13) | `AttentionBlock`, `CrossAttentionBlock`, `SwiGluFfn`, `BertFfn`, `MoeRouterBlock`, `LoraDelta`, `ConvResidualBlock`, `ConformerBlock`, `SwinWindowAttn`, `PatchEmbed`, `DetectionHead`, `EmbeddingLookup`, `BnState`, `VaeAttnBlock` |
| **TupleSlot** (~25) | `Q`, `K`, `V`, `O`, `gate`, `up`, `down`, `base`, `lora_A`, `lora_B`, `router`, `expert_*`, ... |

Architecture-specific name mapping is **declarative data** in `TupleResolver` per-architecture tables, not code in decomposers. Decomposer dispatch operates on tuples (compositions), not on per-name singletons. New architecture = new resolver table row, NOT a new decomposer file.

This collapses the decomposer library to ~10 files: 4 primitive passes + 5 tuple-attestation passes + the resolver. Pre-correction shape had per-name role enum sprawl (`AttentionQuery` separate from `AttentionKey` separate from `MoeRouter` separate from `LoraA` separate from `RopeFreq` — 40+ values, 30+ decomposer files all doing variants of the same math).

## Container decomposer

**SafetensorsContainerDecomposer** (today's `SafetensorsDecomposer`, scope-narrowed). Knows safetensors file format, .pt/.bin/.ckpt variants via `IDonorPackageReader`, package layouts (HF cache, snapshot dir, multi-subdir like Flux's `model_index.json`). Inventories tensors, classifies via `TensorClassifier`, dispatches to layer-type decomposers + metadata + tokenizer + content decomposers.

## Universal layer decomposers (collapsing to primitive/tuple passes per AP-30)

| Decomposer (legacy name) | Tensor roles | Edge participants |
|---|---|---|
| `AttentionQkvLayerDecomposer` | AttentionQuery + AttentionKey | `word_form ↔ word_form` via `model_attention_pattern` |
| `AttentionVoLayerDecomposer` | AttentionValue + AttentionOutput | `word_form ↔ word_form` via `model_attention_pattern` |
| `FfnLayerDecomposer` | FfnGate + FfnUp + FfnDown | `word_form ↔ word_form` via `model_ffn_factor` |
| `EmbeddingLayerDecomposer` | TokenEmbedding | `word_form ↔ word_form` for proximity via `model_concept_similarity`; SIDE-EFFECT: firefly POINTZM per token |
| `LmHeadLayerDecomposer` | LmHead | `word_form` single-participant attestation |
| `LayerNormLayerDecomposer` | LayerNormScale, LayerNormBias | per-tensor analysis attestation (no token edges) |
| `MoeRouterLayerDecomposer` | MoeRouter | `word_form ↔ expert-id metadata` |
| `MoeExpertLayerDecomposer` | MoeExpert(Gate/Up/Down), MoeSharedExpert | `word_form ↔ word_form` with expert-id metadata |
| `LoRAAdapterLayerDecomposer` | LoRA A + B factors | `word_form ↔ word_form` with rank-component metadata |

## Specialist layer decomposers (architecture-specific)

| Decomposer | Where used | Produces |
|---|---|---|
| `CrossAttentionLayerDecomposer` | Vision-language (CLIP, BLIP, Flamingo), diffusion text-conditioning (Flux DiT, SDXL) | Cross-attention QK between two content streams → bridge edges between content modalities |
| `ConvLayerDecomposer` | CNN backbones, U-Net, VAE | Conv kernel filter → spatial pattern attestation in pixel_region content space |
| `ViTPatchAttentionLayerDecomposer` | Vision transformers (ViT, DINOv2, SigLIP) | Patch embedding + attention over patches → `pixel_region ↔ pixel_region` edges |
| `CodecRvqLayerDecomposer` | Audio codecs (EnCodec, SoundStream), MusicGen, AudioCraft | RVQ codebook entries + quantization assignment → codeword transition edges |
| `DetectionHeadLayerDecomposer` | YOLO, DETR, RT-DETR | Bbox regression + class projection → `pixel_region ↔ word_form (class)` edges |
| `DiffusionUnetLayerDecomposer` | Stable Diffusion, SDXL, Flux | Timestep-conditioned denoising; step-transition attestations |

## Metadata decomposers

| Decomposer | Files |
|---|---|
| `ModelConfigDecomposer` | config.json, generation_config.json |
| `ModelIndexDecomposer` | model_index.json (multi-component packages: Flux, Stable Diffusion, Diffusers-format) |
| `TokenizerConfigDecomposer` | tokenizer_config.json, special_tokens_map.json |
| `ModelCardDecomposer` | README.md, MODEL_CARD.md, citation files |

## Tokenizer decomposer

**HuggingFaceTokenizerDecomposer** (refactor of today's `TokenizerMappingPass`). Reads tokenizer.json BPE/WordPiece/SentencePiece variants. For each vocab entry, runs token bytes through `SubstrateTextDecomposer.EmitStatic` → word_form entity. Same vocab token across two models that share it collapses to ONE word_form entity (content-addressed identity).

## Code decomposer

**PythonCodeDecomposer** (lightweight, optional). When model package ships `modeling_*.py` / `configuration_*.py`, ingest as text_composition with code-aware boundaries (treesitter-style or whitespace/identifier-aware). Marginal value for ingestion quality; mostly substrate text consensus completeness.

## Content decomposers per modality

| Decomposer | Status | Produces |
|---|---|---|
| `SubstrateTextDecomposer` | exists (`src/Hartonomous.Core/Text/`) | codepoint → grapheme_cluster → word_form → text_composition tree from UTF-8 bytes |
| `AudioContentDecomposer` | future | WAV/FLAC/MP3 decode, framing, mel/MFCC features → audio_recording → audio_chunk LINESTRINGZM with time/frequency/amplitude axes; alignment to transcript word_forms via CTC/forced-alignment when available |
| `ImageContentDecomposer` | future | PNG/JPEG/WebP decode, patch grid, visual feature extraction; CLIP-style binding to text concepts when available; produces pixel_region with 2D-position/intensity/class axes |
| `VideoContentDecomposer` | future | Container demux, per-frame extraction, possibly motion features; produces video_frame + per-frame pixel_region |

## Seed decomposers (per-corpus)

`docs/specs/decomposers/{ucd-uca,iso639,wordnet,omw,ud,wiktionary,tatoeba}.md`. Each ingests its seed corpus into the substrate via the universal pipeline. Per the trinity-axis taxonomy (`frame/25-TRINITY-AXIS-EMISSION.md`), seed corpora are Axis-1 = seed; their decomposer outputs span all three Axis-2 buckets.

## Composition: how a model package decomposes (Llama 4 Maverick example)

```
SafetensorsContainerDecomposer
  ├─ ModelConfigDecomposer(config.json) → architecture metadata edges
  ├─ ModelCardDecomposer(README.md) → documentation entity
  ├─ TokenizerConfigDecomposer(tokenizer_config.json)
  ├─ HuggingFaceTokenizerDecomposer(tokenizer.json) → word_form entities
  └─ for each tensor (dispatched by TensorRole):
       AttentionQkv / AttentionVo / Ffn / Embedding / LmHead / LayerNorm
       / MoeRouter / MoeExpert layer decomposers
       → token↔token edges in shared word_form substrate
```

Same composition pattern works for CLIP (vision + text + projection), Flamingo (vision + LM + cross-attention bridges), Whisper (audio + text + cross-attention), MusicGen (codec + text + transformer with cross-attention), MiniLM (text-only), BGE (text + pooling head). A model package is a recipe over decomposers, not bespoke code.

Cross-references:
- `frame/05-TRACK2-ATTESTATION-EDGES.md` — per-role-unit attestation edges (the centerpiece correction)
- `frame/21-TRACK1-TRACK2-MODEL-INGESTION.md` — two-track ingestion shape
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — firefly POINTZM emission as EmbeddingLayerDecomposer side-effect
- `frame/25-TRINITY-AXIS-EMISSION.md` — per-decomposer contract template
