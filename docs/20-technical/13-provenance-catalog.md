# Provenance Catalog

**Status:** Canonical (initial set); living document
**Last verified:** 2026-04-29 (entries cross-checked against actual filesystem inventory in `50-reference/04-data-asset-paths.md`)
**Audience:** Decomposer authors selecting provenance codes; substrate operators tuning trust priors; anyone reading audit chains.

---

## Trust prior tuning policy

Trust priors (`provenance.initial_mu`) are starting Glicko-2 ratings for edges from a given source. They reflect substrate-operator judgment about source authority. They can be updated at runtime via substrate function (existing edges keep their accumulated significance state; new attestations from that provenance use the updated prior).

The values below are the initial seed. Customers and operators may override per-deployment.

## Initial provenance rows (curated/authoritative sources)

Seeded by migration `0005_reference_seed`:

| Code | Curator class | initial_mu | Source path |
|---|---|---|---|
| `unicode_consortium` | authoritative_standard | 2000 | `D:\Models\UCD\Public\UCD\latest\` (full FTP mirror) |
| `sil_international` | authoritative_standard | 2000 | `D:\Models\ISO639\` (ISO 639-3 .tab files) |
| `princeton_wordnet` | academic_curated | 1800 | `D:\Models\princeton-wordnet\WordNet-3.0\dict\` |
| `omwn_consortium` | academic_consortium | 1600 | `D:\Models\omw\wns\` |
| `universaldependencies` | academic_consortium | 1600 | `D:\Models\ud-treebanks\ud-treebanks-v2.17\` (339 treebanks) |
| `wiktextract` | community_curated | 1400 | `D:\Models\wiktionary\raw-wiktextract-data.jsonl` |
| `tatoeba` | community_contributed | 1200 | `D:\Models\tatoeba\` |
| `tiny_codes` | community_curated | 1300 | `D:\Models\hub\datasets--nampdn-ai--tiny-codes\snapshots\9aebe5e...\` |
| `system_computed` | system_computed | 1300 | (substrate-derived; e.g., centroids from compositions) |
| `user_session` | user_input | 1000 | (per-session content) |

## Sub-provenance for ingested AI models

Per-model trust priors. Format: `huggingface_model:<canonical_id>` with optional flags for special properties.

The format-and-flag suffix encodes ingestion-relevant details:
- `:awq` — AWQ post-training quantized (lossy)
- `:fp8-native` — FP8 native training quantization (DeepSeek pattern)
- `:pytorch-pickle` — uses `.pt`/`.pth` not safetensors
- `:torchscript` — uses `.torchscript` not safetensors
- `:lora-adapter` — adapter pattern (delta over base model)
- `:diffusion-pipeline` — multi-component diffusion pipeline (transformer + vae + text_encoder + ...)
- `:custom-code` — requires custom modeling/configuration Python modules

### Frontier-tier LLMs (high trust)

| Code | initial_mu | Format | Architecture | Path |
|---|---|---|---|---|
| `huggingface_model:llama-4-maverick-17b-128e` | 1700 | safetensors (55 shards) | Llama4ForConditionalGeneration | `D:\Models\hub\ymodels--meta-llama--Llama-4-Maverick-17B-128E\snapshots\10751cb97a4d7c90f7ed89196b98eb8220cfa1c2\` |
| `huggingface_model:qwen3-coder-480b-a35b` | 1700 | safetensors (241 shards) | Qwen3MoeForCausalLM | `D:\Models\hub\wQwen3-Coder-480B-A35B-Instruct\` |
| `huggingface_model:deepseek-v3.2-speciale:fp8-native` | 1650 | safetensors (163 shards) | DeepseekV32ForCausalLM | `D:\Models\hub\zDeepSeek-V3.2-Speciale\` |

**DeepSeek-V3.2-Speciale rationale for slightly lower prior (1650 vs 1700):** native FP8 quantization (E4M3) is lossy relative to BF16. The substrate ingests its attestations but with a small trust-prior penalty to reflect quantization-derived uncertainty. Sub-provenance flag `:fp8-native` allows queries to filter it specifically.

### Mid-tier LLMs

| Code | initial_mu | Format | Architecture | Path |
|---|---|---|---|---|
| `huggingface_model:qwen3-coder-30b-a3b-instruct` | 1600 | safetensors (16 shards) | Qwen3MoeForCausalLM | `D:\Models\hub\models--Qwen--Qwen3-Coder-30B-A3B-Instruct\snapshots\b2cff64...\` |
| `huggingface_model:deepseek-coder-33b-instruct` | 1600 | safetensors (7 shards) | LlamaForCausalLM (DeepSeek lineage) | `D:\Models\hub\models--deepseek-ai--deepseek-coder-33b-instruct\snapshots\61dc97b...\` |
| `huggingface_model:deepseek-coder-v2-lite-instruct:custom-code` | 1550 | safetensors (4 shards) + custom Py | DeepseekV2ForCausalLM | `D:\Models\hub\models--deepseek-ai--DeepSeek-Coder-V2-Lite-Instruct\snapshots\e434a23...\` |
| `huggingface_model:qwen2.5-coder-14b-instruct` | 1500 | safetensors (6 shards) | Qwen2ForCausalLM | `D:\Models\hub\models--Qwen--Qwen2.5-Coder-14B-Instruct\snapshots\aedcc2d...\` |

### Smaller LLMs

| Code | initial_mu | Format | Path |
|---|---|---|---|
| `huggingface_model:qwen2.5-coder-7b-instruct` | 1500 | safetensors (4 shards) | `...models--Qwen--Qwen2.5-Coder-7B-Instruct\snapshots\c03e6d3...\` |
| `huggingface_model:qwen2.5-coder-3b-instruct` | 1450 | safetensors (2 shards) | `...models--Qwen--Qwen2.5-Coder-3B-Instruct\snapshots\488639f...\` |
| `huggingface_model:qwen2.5-coder-7b-instruct:awq` | 1300 | safetensors (2 shards, AWQ-quantized) | `...models--Qwen--Qwen2.5-Coder-7B-Instruct-AWQ\snapshots\8e8ed24...\` |
| `huggingface_model:qwen2.5-coder-3b-instruct:awq` | 1250 | safetensors (1 shard, AWQ-quantized) | `...models--Qwen--Qwen2.5-Coder-3B-Instruct-AWQ\snapshots\5d26593...\` |

**AWQ variants are SKIP by default** per ADR-002. Listed for completeness; ingestion only if `quantization_damage` arena research is desired.

### Embedding models

| Code | initial_mu | Format | Path |
|---|---|---|---|
| `huggingface_model:qwen3-embedding-4b` | 1500 | safetensors (2 shards) | `...models--Qwen--Qwen3-Embedding-4B\snapshots\5cf2132...\` |
| `huggingface_model:qwen3-embedding-0.6b` | 1450 | safetensors (1) | `...models--Qwen--Qwen3-Embedding-0.6B\snapshots\c54f2e6...\` |
| `huggingface_model:qwen3-vl-embedding-8b` | 1500 | safetensors (4 shards) | `...models--Qwen--Qwen3-VL-Embedding-8B\snapshots\a12d611...\` |
| `huggingface_model:qwen3-vl-embedding-2b` | 1450 | safetensors (1) | `...models--Qwen--Qwen3-VL-Embedding-2B\snapshots\929a0c3...\` |
| `huggingface_model:jina-code-embeddings-1.5b` | 1450 | safetensors (1) | `...models--jinaai--jina-code-embeddings-1.5b\snapshots\39aeb4f...\` |
| `huggingface_model:sentence-transformers-all-minilm-l6-v2` | 1300 | safetensors (1) | `...models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed...\` |

### Reranker models

| Code | initial_mu | Format | Path |
|---|---|---|---|
| `huggingface_model:qwen3-reranker-4b` | 1500 | safetensors (2 shards) | `...models--Qwen--Qwen3-Reranker-4B\snapshots\f16fc5d...\` |
| `huggingface_model:qwen3-reranker-0.6b` | 1450 | safetensors (1) | `...models--Qwen--Qwen3-Reranker-0.6B\snapshots\6e9e698...\` |
| `huggingface_model:qwen3-vl-reranker-8b` | 1500 | safetensors (4 shards) | `...models--Qwen--Qwen3-VL-Reranker-8B\snapshots\8e52ab8...\` |
| `huggingface_model:qwen3-vl-reranker-2b` | 1450 | safetensors (1) | `...models--Qwen--Qwen3-VL-Reranker-2B\snapshots\76219da...\` |
| `huggingface_model:jina-reranker-v3:custom-code` | 1450 | safetensors (1) + custom Py | `...models--jinaai--jina-reranker-v3\snapshots\050e171...\` |
| `huggingface_model:zerank-2:custom-code` | 1450 | safetensors (2 shards) + custom Py | `...models--zeroentropy--zerank-2\snapshots\9ae8623...\` |

### Vision models

| Code | initial_mu | Format | Architecture | Path |
|---|---|---|---|---|
| `huggingface_model:florence-2-large:custom-code` | 1500 | safetensors + pytorch_model.bin + custom Py | Florence2ForConditionalGeneration | `D:\Models\hub\Florence-2-large\` |
| `huggingface_model:florence-2-base:custom-code` | 1450 | safetensors + pytorch_model.bin + custom Py | Florence2ForConditionalGeneration | `D:\Models\hub\Florence-2-base\` |
| `huggingface_model:grounding-dino-base` | 1450 | safetensors + pytorch_model.bin | GroundingDinoForObjectDetection | `D:\Models\hub\Grounding-DINO-Base\` |
| `huggingface_model:detr-resnet-101` | 1400 | safetensors + pytorch_model.bin | DetrForObjectDetection | `D:\Models\hub\DETR-ResNet-101\` |
| `huggingface_model:conditional-detr-r50` | 1400 | safetensors + pytorch_model.bin | ConditionalDETRForObjectDetection | `D:\Models\hub\Conditional-DETR-R50\` |
| `huggingface_model:rt-detr-v1-r101` | 1400 | safetensors only | RTDetrForObjectDetection | `D:\Models\hub\RT-DETR-v1-R101\` |
| `ultralytics:yolo11x:pytorch-pickle` | 1400 | yolo11x.pt | Ultralytics YOLO11x | `D:\Models\hub\yolo11x\yolo11x.pt` |
| `ultralytics:yolo11x:torchscript` | 1400 | yolo11x.torchscript | Ultralytics YOLO11x (TorchScript) | `D:\Models\yolo11x.torchscript` |

YOLO11x exists in two formats. Both are the same underlying model; ingest one of them, not both, unless researching format-divergence in `serialization_consistency` arena.

### Audio models

| Code | initial_mu | Format | Architecture | Path |
|---|---|---|---|---|
| `huggingface_model:sam-audio-large:pytorch-pickle` | 1500 | checkpoint.pt | (Meta SAM-audio) | `...models--facebook--sam-audio-large\snapshots\5f2cd3a...\` |
| `huggingface_model:granite-speech-3.3-8b:lora-adapter` | 1500 | safetensors (9 shards) + adapter_model.safetensors | GraniteSpeechForConditionalGeneration | `...models--ibm-granite--granite-speech-3.3-8b\snapshots\315afb3...\` |
| `huggingface_model:canary-qwen-2.5b` | 1450 | safetensors (1) | (NVIDIA Canary) | `...models--nvidia--canary-qwen-2.5b\snapshots\6cfc37e...\` |
| `huggingface_model:fish-speech-1.5:pytorch-pickle` | 1400 | model.pth + firefly-gan-vq-fsq-8x1024-21hz-generator.pth + tokenizer.tiktoken | (Fish-specific) | `...models--fishaudio--fish-speech-1.5\snapshots\275a984...\` |
| `huggingface_model:music-flamingo-hf` | 1450 | safetensors (4 shards) | AudioFlamingo3ForConditionalGeneration | `...models--nvidia--music-flamingo-hf\snapshots\e29cfe9...\` |

**SAM-audio-large and Fish-Speech-1.5 are NOT in safetensors format.** Per `:pytorch-pickle` flag, these require a PyTorch-pickle decomposer. The standard SafetensorsDecomposer cannot ingest them. Either:
1. Implement a sibling `PytorchPickleDecomposer` that handles `.pt`/`.pth` via `torch.load(..., weights_only=True)`, OR
2. Convert these models to safetensors before ingestion (out-of-band step), OR
3. Skip them until decomposer support exists.

**Granite-Speech LoRA adapter pattern:** The base model is the 9-shard safetensors; `adapter_model.safetensors` is a LoRA delta. Ingestion needs adapter-aware composition: base model edges + adapter delta = effective weights. Sub-provenance `:lora-adapter` flags this for the recomposer's awareness.

### Diffusion model

| Code | initial_mu | Format | Architecture | Path |
|---|---|---|---|---|
| `huggingface_model:flux.2-dev:diffusion-pipeline` | 1500 | Multi-component (transformer + ae + text_encoder + tokenizer + vae + scheduler) | FLUX.2-dev | `D:\Models\hub\xmodels--black-forest-labs--FLUX.2-dev\snapshots\6aab690...\` |

FLUX.2-dev is NOT a single safetensors file. It's a HuggingFace `diffusers` pipeline:
- `flux2-dev.safetensors` (main transformer)
- `ae.safetensors` (autoencoder)
- `text_encoder/` (subdirectory with its own model files)
- `tokenizer/`
- `transformer/` (subdirectory variant?)
- `vae/`
- `scheduler/`
- `model_index.json` (orchestrates components)

The `:diffusion-pipeline` flag tells the safetensors decomposer to recurse into subdirectories and ingest each component as separate sub-provenance (e.g., `huggingface_model:flux.2-dev:transformer`, `huggingface_model:flux.2-dev:vae`). Then cross-component edges (text encoder → transformer conditioning) are first-class substrate edges.

## Excluded from initial ingestion

### GGUF quantized (skip per ADR-002)

| Path | Quantization |
|---|---|
| `D:\Models\Active\Qwen3-Coder-30B-Q4_K_M.gguf` | Q4_K_M (4-bit) |
| `D:\Models\Active\Qwen3-Embedding-0.6B-Q8_0.gguf` | Q8_0 (8-bit) |
| `D:\Models\Active\Qwen3-Embedding-4B-Q4_K_M.gguf` | Q4_K_M |
| `D:\Models\Active\Qwen3-Embedding-4B-Q8_0.gguf` | Q8_0 |
| `D:\Models\Active\Qwen3-Reranker-0.6B-Q8_0.gguf` | Q8_0 |
| `D:\Models\Active\Qwen3-Reranker-4B-Q4_K_M.gguf` | Q4_K_M |
| `D:\Models\Active\Qwen3-Reranker-4B-Q8_0.gguf` | Q8_0 |
| `D:\Models\tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf` | Q4_K_M |

All have full-precision counterparts in `D:\Models\hub\models--Qwen--*` (except TinyLlama, which has no in-hub counterpart and would need a fresh full-precision download if desired).

### Not models / not substrate fuel

- `D:\Models\qdrant\` — running Qdrant DB instance
- `D:\Models\xet\` — HF Hub xet (large-file-storage) cache
- `D:\Models\converttoawq.py`, `download_*.py`, `quantize.py`, `quantizer.py` — helper scripts
- `D:\Models\token`, `D:\Models\stored_tokens` — HF auth tokens (sensitive)

## Architecture metadata source

`D:\Models\model_catalog.json` contains pre-computed architecture descriptors for 21 of the hub models, with tensor categorization (TOKEN_EMBEDDING, ATTENTION_OUTPUT, LAYER_NORM, FFN_UP, FFN_GATE, FFN_DOWN, MOE_SHARED_EXPERT, MOE_EXPERT_UP, MOE_EXPERT_DOWN, MODALITY_PROJECTION, etc.) and dtypes. The substrate's safetensors decomposer can use this catalog as a fast-path for those models; for the rest, parse `config.json` directly.

Catalog coverage as of 2026-04-29: DeepSeek-V3.2-Speciale, FLUX.2-dev (via SHA), Qwen3-Coder-480B, Conditional-DETR-R50, DETR-ResNet-101, Florence-2-base, Florence-2-large, Grounding-DINO-Base, sam-audio-large (via SHA), fish-speech-1.5 (via SHA), Granite-Speech-3.3-8b (via SHA), Llama-4-Maverick-17B-128E (×2), Canary-Qwen-2.5b (via SHA), Music-Flamingo-HF (via SHA), Qwen2.5-Coder 14B/3B/7B (via SHA), Qwen3-Coder-30B-A3B (via SHA), MiniLM-L6-v2 (via SHA), RT-DETR-v1-R101.

NOT in catalog (must parse config.json directly): all Qwen3 Embedding/Reranker variants, Qwen3-VL Embedding/Reranker variants, Jina models, Zerank-2, AWQ variants, YOLO11x.

## Cross-references

- Verified data asset paths: `50-reference/04-data-asset-paths.md`
- Significance pillar: `10-architecture/04-significance-glicko.md`
- Schema: `20-technical/00-schema-reference.md`
- ADR-002 (atom vocabulary, AWQ/GGUF policy): `60-status/04-decisions-log.md`
- Decomposer contract: `10-architecture/05-decomposer-contract.md`
