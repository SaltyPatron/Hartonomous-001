# Recomposers / synthesis-from-consensus

Source: `docs/00-substrate-spec.md` §VI, `docs/specs/engine/generation-and-transformation.md`, `docs/specs/recomposers/*.md`, AP-5 / AP-28.

## What recomposition IS

The recomposer **synthesizes weights from substrate consensus across all ingested models**, NOT round-trip from one source's stored content. User specifies arbitrary target architecture spec; recomposer projects substrate consensus into the architecture's tensor basis and emits standard safetensors.

Inverse of decomposer library: each layer-type decomposer has a reciprocal layer-type synthesizer.

## The synthesis surface

`RecomposeAsync(TargetArchitectureSpec, RecompositionOptions, CancellationToken)` → `SafetensorsFile`.

`TargetArchitectureSpec` is **fully arbitrary**: layer count, hidden dim, attention head count, attention head dim, FFN intermediate size, MoE expert count and routing, LoRA ranks, vocabulary size and tokenizer choice, modality mix (text only / text+vision / text+audio / arbitrary combination), attention bias style (RoPE / ALiBi / learned), normalization style (LayerNorm / RMSNorm), activation function. Architectures not previously seen during ingestion are valid inputs; substrate's content-addressed consensus has no notion of "this architecture is supported."

`RecompositionOptions` carries:
- Arena weighting (which arenas the consensus should be weighted by)
- Significance threshold (below which attestations don't contribute)
- Source filter (restrict to subset of ingested models if desired)
- Quantization target (output dtype: F32, F16, BF16, F8_E4M3, F8_E5M2, etc.)
- Recipe identifier for audit trail

## Per-layer-type synthesizers (reciprocal of decomposer library)

| Synthesizer | Target tensor role | Synthesis algorithm |
|---|---|---|
| `AttentionQkvLayerSynthesizer` | AttentionQuery + AttentionKey | Low-rank approximation `min ‖S - QK^T‖²` over sparse attestation matrix S where `S[a][b]` = consensus mu of `model_attention_pattern(token_a, token_b)` edges filtered by arena + `EdgeRatingEvent` attribution `(Linear, AttentionBlock, {Q,K})` |
| `AttentionVoLayerSynthesizer` | AttentionValue + AttentionOutput | Same low-rank fit over `model_attention_pattern` filtered by `(Linear, AttentionBlock, {V,O})` |
| `FfnLayerSynthesizer` | FfnGate + FfnUp + FfnDown | KV-memory inversion over `model_ffn_factor` consensus filtered by `(Linear, SwiGluFfn, {gate,up,down})`; honest abstention on under-attested rows |
| `EmbeddingLayerSynthesizer` | TokenEmbedding | PCA over per-token attestation participation; alternatively firefly cluster centroids (per `frame/06`) projected back to hidden_dim via inverse Laplacian eigenmap. Mode 1 (centroid consensus, requires anchor-Procrustes alignment) vs Mode 2 (shape-archetype matching via Hausdorff/Fréchet, rotation-aware per-entity, no alignment needed). |
| `LmHeadLayerSynthesizer` | LmHead | PCA / least-squares over `model_concept_similarity` attestations filtered by `(Linear, EmbeddingLookup, lm_head)` |
| `LayerNormLayerSynthesizer` | LayerNormScale | Per-feature parameter from analysis-surface attestations |
| `MoeRouterLayerSynthesizer` | MoeRouter | Synthesize routing matrix from token↔expert attestation strengths; expert IDs may be remapped per target |
| `MoeExpertLayerSynthesizer` | MoeExpert(Gate/Up/Down) | Per-expert FFN synthesis using FfnLayerSynthesizer's algorithm scoped to expert's attestation set |
| `LoRAAdapterLayerSynthesizer` | LoRA (A, B) factor pair | Low-rank synthesis preserving A·B factorization at user-specified rank |
| Specialist synthesizers | Conv, ViTPatch, CodecRVQ, DetectionHead, CrossAttention, DiffusionUnet | Each reciprocal to its layer-type decomposer |

## Honest abstention at synthesis

When attestation density for a tensor cell is below threshold, cell stays at exact zero. Output is genuinely sparse; recomposer never invents weights to cover gaps. Output metadata reports per-tensor coverage statistics (% cells synthesized, mean attestation density) for downstream evaluation.

This is inference-side honest-abstention principle applied at synthesis time. What makes substrate-rebuilt models defensible: every weight traceable to specific attestations from specific models in specific arenas with specific Glicko mu.

## Standards-compliant output

Standard safetensors file: header (tensor name, dtype, shape) + binary tensor blob, byte-compatible with HuggingFace transformers, vLLM, llama.cpp loaders. Audit metadata in safetensors header records:
- Recipe ID
- Arena weighting
- Significance threshold
- Content-addressed hash of recomposition recipe (so same input yields same output bytes, subject to synthesis recomposer's relaxed determinism per `frame/23-DETERMINISM-LAW-6.md` §XI)

vLLM, llama.cpp, HuggingFace transformers don't know anything about the substrate. They load synthesized safetensors and run it as a normal model. The world's existing inference infrastructure is the substrate's distribution surface for free.

## Generation IS composition assembly from inference paths (NOT token sampling)

Source: `docs/specs/engine/generation-and-transformation.md`.

### Text generation (detailed pipeline)

Given inference path terminating at target sense/concept entities:
1. **Sense to lemma**: each sense entity connects to lemma entities via `has_sense` edges. Select lemma for target language via `entity_language` junction.
2. **Lemma to surface form**: each lemma connects to inflected form entities via `has_form` edges. Select form matching required morphological features for syntactic position (case from dependency edge type; number from referent plurality; tense from temporal context; person from subject reference; gender from referent gender if language requires agreement). Morphological features from `morph_feature` reference table drive selection.
3. **Surface forms to word order**: UD syntactic patterns determine ordering. English SVO, Japanese SOV, Arabic VSO. Substrate knows from UD treebank patterns — most significant syntactic pattern for target language determines word order.
4. **Morphological composition**: for agglutinative languages (Turkish, Finnish, Hungarian), surface form composed from morpheme entities (root + affixes) following morphological composition rules in substrate.
5. **Punctuation and spacing**: target language conventions (UCD break properties + UD MISC annotations).
6. **Codepoint composition**: final output is sequence of codepoint entities — tier-0 from UCD seed. NEW composition entity in substrate.

### Image generation

1. Concept → visual features (visual concept entities connect to spatial compositions via edges)
2. Spatial arrangement (above / below / left-of / contains / overlaps edge types)
3. Color values (cascade-compressed shared entities — sky-blue pixel = one shared entity referenced by position)
4. Resolution construction (pixel grid from spatial compositions at target resolution)
5. Output via `ImageRecomposer` into target format (PNG, JPEG, etc.)

### Audio generation

1. Concept → audio features (spectral/temporal patterns)
2. Temporal arrangement
3. Waveform construction from LINESTRINGZM audio entities
4. Output via `AudioRecomposer` into target format (WAV, MP3, etc.)

### Multi-modal generation

Composes outputs from multiple modality-specific paths:
- Text + image = captioned image or illustrated text
- Text + audio = narrated text or transcribed audio
- Image + audio = video frame with soundtrack

Cross-modal alignment edges (from ingestion-time analysis) guide synchronization.

## Transformation IS inference + generation through substrate

### Language translation (NOT separate system)

1. Decompose source text into substrate entities
2. Traverse cross-lingual edges from source-language sense entities to target-language sense entities (via OMW synset alignments, Wiktionary translations, model-derived cross-lingual edges)
3. Select target-language senses with highest significance in `translation_quality` arena
4. Generate target text using target language's morphological rules and syntactic patterns

Quality depends directly on density and significance of cross-lingual edges. More OMW / Wiktionary translations / Tatoeba parallel sentences = better translation.

### Modality conversion (analogous to translation across modalities)

- **TTS**: text → phoneme/pronunciation entities (Wiktionary sounds, IPA edges) → traverse pronunciation-to-audio edges (Tatoeba audio alignment, model-derived speech edges) → compose audio waveform
- **STT**: audio → spectral/temporal entities → traverse audio-to-phoneme edges (forced alignment data, model-derived) → phonemes to words to senses → generate text
- **Image-to-text (captioning)**: image → visual entities (objects, regions, attributes) → traverse visual-to-semantic edges (model-derived: object detection, visual grounding) → generate text describing semantic entities
- **Text-to-image**: text → semantic entities → traverse semantic-to-visual edges → compose image from visual entities

### Summarization

"Just turning up the significance threshold." Inference with tighter min_significance + shorter path length budget.

### Paraphrase / Rewrite

Re-traverse from same senses through DIFFERENT lemma/form/syntax paths. Arena dynamics ensure multiple valid phrasings have significance; paraphraser selects from alternatives substrate already knows about.

### Style transfer

Style = register/formality/genre classifications from classification vocabulary. Add style constraint (filter by register classification) to generation step. Substrate edges distinguish formal vs informal, technical vs casual.

## Recomposer family

All implement `IRecomposer<TTarget>`. All share `BaseRecomposer` logic for traversal and collection. Only format-specific encoding in concrete implementation.

| Recomposer | Input | Output | Process |
|---|---|---|---|
| `TextRecomposer` | composition entity (sequence of codepoint entities) | UTF-8 byte string | Walk sequence, collect codepoint values, encode to UTF-8. Round-trip: decompose output, compare entity hashes to originals. |
| `ImageRecomposer` | spatial composition entity (pixel grid references) | PNG/JPEG/etc bytes | Walk spatial composition, collect color values at positions, encode to target format |
| `AudioRecomposer` | temporal composition entity (waveform LINESTRINGZM references) | WAV/MP3/etc bytes | Walk temporal sequence, extract amplitude values from LINESTRINGZM, encode to target format |
| `SafetensorsRecomposer` | model architecture entity + tensor entities | safetensors file bytes (header JSON + binary buffer) | Reconstruct header from tensor metadata entities, reconstruct buffer from weight data |
| `VideoRecomposer` | video composition entity | video file bytes | Composition of ImageRecomposer (per frame) + AudioRecomposer (per track) + temporal sync |

## What replaces phantom-scatter recomposer (AP-5 / AP-28)

Current `src/Hartonomous.Recomposers/SafetensorsRecomposer.cs:239-373` `AssembleTensorBytesAsync` is single-source phantom-scatter:
- Walks `has_constituent` children of each tensor (phantom per-role-unit entities)
- Reads their stored `contour` physicality
- Scatters values at row positions
- Falls back to SVD reconstruction via `has_rank_component` edges to phantom `svd_rank_component` entities

This works only for round-tripping a single source model whose phantoms were stored at ingest. **Substrate-synthesis-from-consensus is impossible with this path.**

Replacement: synthesis library above, dispatched by target tensor role from `TargetArchitectureSpec`. Phantom-scatter paths deleted as part of phantom debt removal.

Cross-references:
- `frame/04-DECOMPOSER-ARCHITECTURE.md` — layer-type decomposers (synthesizers are reciprocals)
- `frame/05-TRACK2-ATTESTATION-EDGES.md` — attestation edges that consensus is computed over
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — Mode 1 (centroid) vs Mode 2 (shape) embedding synthesis
- `frame/16-COGNITIVE-SURFACE.md` — `hartonomous.recompose.*` SQL API
- `frame/23-DETERMINISM-LAW-6.md` — relaxed-determinism budget at synthesis (constrained, not strict)
- `frame/24-ANTI-PATTERNS-CATALOG.md` — AP-5 (round-trip framing), AP-28 (phantom-scatter recomposer)
