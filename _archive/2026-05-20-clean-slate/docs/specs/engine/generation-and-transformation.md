# Generation and Transformation Specification

## Generation

Generation is composition assembly from inference paths. Not token-by-token sampling from a probability distribution. The substrate already contains the knowledge; generation selects and arranges it.

### Text Generation (Detailed)

Given an inference path that terminates at target sense/concept entities:

1. **Sense to lemma**: each sense entity connects to lemma entities via `has_sense` edges. Select the lemma for the target language (via `entity_language` junction table lookup to `language` reference table).

2. **Lemma to surface form**: each lemma entity connects to inflected form entities via `has_form` edges. Select the form matching the required morphological features for this syntactic position:
   - What case? (from the dependency edge type — nsubj usually nominative, obj usually accusative in case-marking languages, looked up via `deprel` reference table)
   - What number? (from the referent's plurality)
   - What tense? (from the temporal context)
   - What person? (from the subject reference)
   - What gender? (from the referent's gender if the language requires agreement)

   The morphological features from the `morph_feature` reference table drive this selection. Every feature combination is an entity with junction table entries linking it to its features. The right form is the one whose feature set matches the syntactic context's requirements.

3. **Surface forms to word order**: the syntactic patterns from UD determine ordering. English is SVO, Japanese is SOV, Arabic is VSO (typically). The substrate knows this from the UD treebank patterns — the most significant syntactic pattern for the target language determines word order.

4. **Morphological composition**: for languages with agglutination (Turkish, Finnish, Hungarian), the surface form may need to be composed from morpheme entities (root + affixes) following the morphological composition rules in the substrate.

5. **Punctuation and spacing**: determined by the target language's conventions (stored as UCD break properties in reference tables and UD MISC annotations).

6. **Codepoint composition**: the final output is a sequence of codepoint entities -- tier-0 entities from the UCD seed. This is a new composition entity in the substrate.

### Image Generation

Given an inference path that terminates at visual concept entities:

1. **Concept to visual features**: visual concept entities (from model-derived edges, image decomposition) connect to spatial compositions (patches, regions, textures) via edges.

2. **Spatial arrangement**: compose patches according to spatial edge types (above, below, left-of, contains, overlaps).

3. **Color values**: select from cascade-compressed color value entities. Sky-blue pixel = one shared entity referenced by position.

4. **Resolution construction**: build pixel grid from spatial compositions at the target resolution.

5. **Output**: pixel grid recomposed by the `ImageRecomposer` into the target format (PNG, JPEG, etc.).

### Audio Generation

Given an inference path that terminates at audio concept entities:

1. **Concept to audio features**: audio concept entities connect to spectral/temporal patterns via edges.

2. **Temporal arrangement**: compose audio segments according to temporal sequence.

3. **Waveform construction**: build waveform from LinestringZM audio entities.

4. **Output**: waveform recomposed by the `AudioRecomposer` into the target format (WAV, MP3, etc.).

### Multi-Modal Generation

Composes outputs from multiple modality-specific generation paths:
- Text + image = captioned image or illustrated text.
- Text + audio = narrated text or transcribed audio.
- Image + audio = video frame with soundtrack.

Cross-modal alignment edges (from ingestion-time analysis) guide synchronization.

## Transformation

Transformation is converting content from one representation or modality to another, through the substrate.

### Language Translation

Translation is NOT a separate system. It is inference + generation through the substrate's cross-lingual structure.

1. **Decompose source text** into substrate entities (codepoints -> morphemes -> words -> senses).
2. **Traverse cross-lingual edges** from source-language sense entities to target-language sense entities (via OMW synset alignments, Wiktionary translations, model-derived cross-lingual edges).
3. **Select target-language senses** with highest significance in the `translation_quality` arena.
4. **Generate target text** using the target language's morphological rules and syntactic patterns.

The quality of translation depends directly on the density and significance of cross-lingual edges in the substrate. More seed data (OMW, Wiktionary translations, Tatoeba parallel sentences) = better translation.

### Modality Conversion

Analogous to translation but across modalities instead of languages:

**Text to speech**:
1. Decompose text into phoneme/pronunciation entities (via Wiktionary sounds, IPA edges).
2. Traverse pronunciation-to-audio edges (from Tatoeba audio alignment, model-derived speech edges).
3. Compose audio waveform from phoneme audio entities.

**Speech to text**:
1. Decompose audio into spectral/temporal entities.
2. Traverse audio-to-phoneme edges (from forced alignment data, model-derived speech edges).
3. Map phonemes to words to senses.
4. Generate text from sense entities.

**Image to text (captioning)**:
1. Decompose image into visual entities (objects, regions, attributes).
2. Traverse visual-to-semantic edges (from model-derived edges: object detection, visual grounding).
3. Generate text describing the semantic entities.

**Text to image**:
1. Decompose text into semantic entities.
2. Traverse semantic-to-visual edges.
3. Compose image from visual entities.

In every case, the transformation is traversal through the substrate's edge graph from one modality's entities to another's.

### Summarization

Summarization = inference with a tighter significance threshold and shorter path length budget.

1. Decompose source content.
2. Traverse with higher minimum significance (only the most important entities survive).
3. Generate output from the reduced set.

The significance field naturally prioritizes the most important content. Summarization is just turning up the significance threshold.

### Paraphrase / Rewrite

Paraphrase = inference that starts from the same sense entities but selects different surface forms / syntactic patterns.

1. Decompose source text to sense level.
2. Re-traverse from the same senses through DIFFERENT lemma/form/syntax paths.
3. Generate output from the alternative path.

The arena dynamics ensure that multiple valid phrasings have significance -- the paraphraser selects from alternatives that the substrate already knows about.

### Style Transfer

Style = register/formality/genre classifications from the classification vocabulary.

1. Decompose source.
2. Add a style constraint to the generation step (e.g., filter by register classification).
3. The substrate's edges distinguish formal vs informal usages, technical vs casual vocabulary, etc.
4. Generate with the style constraint active.

## Recomposers (Output Formatting)

Each modality has a recomposer that converts substrate composition entities into output format bytes.

### `TextRecomposer`
- Input: composition entity (sequence of codepoint entities).
- Output: UTF-8 byte string.
- Process: walk sequence, collect codepoint values, encode to UTF-8.
- Round-trip: decompose output, compare entity hashes to originals.

### `ImageRecomposer`
- Input: spatial composition entity (pixel grid references).
- Output: PNG/JPEG/etc. bytes.
- Process: walk spatial composition, collect color values at positions, encode to target format.

### `AudioRecomposer`
- Input: temporal composition entity (waveform LinestringZM references).
- Output: WAV/MP3/etc. bytes.
- Process: walk temporal sequence, extract amplitude values from LinestringZM, encode to target format.

### `SafetensorsRecomposer`
- Input: model architecture entity + tensor entities.
- Output: safetensors file bytes (header JSON + binary buffer).
- Process: reconstruct header from tensor metadata entities, reconstruct buffer from weight data.

### `VideoRecomposer`
- Composition of `ImageRecomposer` (per frame) + `AudioRecomposer` (per track) + temporal sync.

All recomposers implement `IRecomposer<TTarget>`. All share `BaseRecomposer` logic for traversal and collection. Only format-specific encoding is in the concrete implementation.

## What Makes This Work

The substrate can generate, translate, transform, summarize, paraphrase, and style-transfer because:

1. **All knowledge is explicit edges**, not opaque matrix weights.
2. **Cross-lingual, cross-modal, cross-register connections are first-class edges**, not separate models.
3. **Significance ranks alternatives**, so the system always has multiple options and can select the best one for the context.
4. **Every output is explainable** -- trace the path from input to output through specific entities and edges.
5. **Every output is a new substrate entity** -- the substrate learns from its own outputs when arena updates are applied.
6. **Quality improves with more content** -- more ingested data = more edges = more alternatives = better selection. No retraining needed.
