# Analysis Passes

**Status**: ✅ Complete

Specification for all 43 analysis passes across 4 modalities. Each pass consumes ingested entities, performs analysis, and writes results back as new edges, significance entries, or physicalities.

> **Naming Convention**: This spec uses dotted canonical IDs for pass identification (e.g., `text.morphological_analysis`, `image.edge_detection`). The C# class names use PascalCase (e.g., `MorphologicalAnalysis`, `FeatureExtraction`). The modality domain specs (e.g., [text.md](../modalities/text.md), [image.md](../modalities/image.md)) use descriptive pass names with a `Pass` suffix (e.g., `NERPass`, `EdgeDetectionPass`, `FFTPass`). All three naming styles refer to the same passes. The canonical PassId is the authoritative identifier used in `monitor.phase_status` and progress logging.

---

## Pass Execution Model

All passes inherit from `BaseAnalysisPass` and implement `IAnalysisPass`. The phase runner executes passes after seed ingestion completes. Passes run within the `SignificanceField` phase (Phase 4) or as sub-operations of specific decomposer phases.

**Execution order**: Dependency-ordered within each modality. Cross-modality passes (e.g., `audio.cross_modal_alignment`) run after their dependency modalities complete.

**Batch processing**: Every pass queries entities via `QueryEntitiesInBatchesAsync`, processes each batch, and writes results via the `IIngestionPipeline`. Same pipeline, same stored procedures, same transaction boundaries as decomposers.

---

## Text Passes (7)

### 1. Morphological Analysis

| Field | Value |
|-------|-------|
| PassId | `text.morphological_analysis` |
| Input | `word_form`, `lemma` entities |
| Output | Edges: `has_morpheme` (word → morpheme entities). Junctions: `entity_morph_feature` entries. |
| Dependencies | None (runs on seed-ingested UD data) |
| SP Calls | `batch_create_edges`, `populate_junction('entity_morph_feature', ...)` |
| Volume | ~500K edges (one per morphological decomposition) |
| Complexity | O(N) — one pass per word entity |

Decomposes inflected word forms into morpheme sequences. Uses UD morphological feature annotations (already in `entity_morph_feature` from seed) to identify affixes, stems, and roots. Creates `morpheme` entities and `has_morpheme` edges.

### 2. Dependency Parsing

| Field | Value |
|-------|-------|
| PassId | `text.dependency_parsing` |
| Input | `ud_sentence`, `ud_token` entities |
| Output | Edges: syntactic dependency edges (`nsubj`, `obj`, `amod`, etc.) already created by UD decomposer. This pass computes edge geometries for existing edges. Physicalities: edge.geom LINESTRINGZM for each dependency edge. |
| Dependencies | None |
| SP Calls | `create_physicality` (for edge trajectory geometries) |
| Volume | ~2M physicality entries (one per dependency edge) |
| Complexity | O(E) — one pass per dependency edge |

Computes relational geometry (`edge.geom`) for syntactic dependency edges. The UD decomposer creates the edges; this pass computes their S3 trajectory through participant positions.

### 3. Semantic Similarity

| Field | Value |
|-------|-------|
| PassId | `text.semantic_similarity` |
| Input | `synset`, `lemma` entities |
| Output | Significance entries for semantic edges. Arena: `semantic_relevance`. |
| Dependencies | `text.morphological_analysis` |
| SP Calls | `initialize_significance`, `record_comparison` |
| Volume | ~1M significance initializations |
| Complexity | O(S²) per lexname cluster — bounded by lexname partitioning |

Computes initial semantic similarity significance within WordNet lexname categories. Synsets within the same lexname cluster compete in the `semantic_relevance` arena. Initial mu from provenance trust prior.

### 4. Cross-Lingual Alignment

| Field | Value |
|-------|-------|
| PassId | `text.cross_lingual_alignment` |
| Input | `lemma` entities with `entity_language` junctions, OMW cross-lingual edges |
| Output | Significance entries for translation edges. Arena: `translation_quality`. |
| Dependencies | None |
| SP Calls | `initialize_significance` |
| Volume | ~500K significance initializations (one per translation edge) |
| Complexity | O(T) — one pass per translation edge |

Initializes significance for OMW cross-lingual alignment edges in the `translation_quality` arena. Initial mu from OMW provenance trust prior.

### 5. Frequency Analysis

| Field | Value |
|-------|-------|
| PassId | `text.frequency_analysis` |
| Input | `word_form`, `lemma` entities |
| Output | Significance entries. Arena: `frequency_significance`. |
| Dependencies | None |
| SP Calls | `initialize_significance` |
| Volume | ~200K significance entries |
| Complexity | O(N) — one pass per word/lemma entity |

Uses sequence table reference counts (`count` column) and junction table mu values to compute frequency-based significance. High-frequency words get higher initial mu in the `frequency_significance` arena.

### 6. Collocation Detection

| Field | Value |
|-------|-------|
| PassId | `text.collocation_detection` |
| Input | `ud_sentence` entities |
| Output | Edges: `co_occurrence` between frequently adjacent word entities. |
| Dependencies | `text.frequency_analysis` |
| SP Calls | `batch_create_edges`, `initialize_significance` |
| Volume | ~100K co-occurrence edges |
| Complexity | O(N × W) — N sentences, W average window size |

Scans sentences for statistically significant word co-occurrences (PMI > threshold). Creates `co_occurrence` edges between word pairs that appear together significantly more than chance.

### 7. Etymology Tracing

| Field | Value |
|-------|-------|
| PassId | `text.etymology_tracing` |
| Input | `lemma`, `wikt_sense` entities with Wiktionary etymology data |
| Output | Edges: `etymological_origin` between lemmas. |
| Dependencies | None (runs on Wiktionary-ingested data) |
| SP Calls | `batch_create_edges` |
| Volume | ~200K etymology edges |
| Complexity | O(N) — one pass per lemma with etymology data |

Creates etymological relationship edges from Wiktionary etymology sections. Links cognates, borrowings, and derivations across languages.

---

## Image Passes (8)

### 1. Feature Extraction

| Field | Value |
|-------|-------|
| PassId | `image.feature_extraction` |
| Input | `pixel_region` entities |
| Output | Physicalities: HOG descriptors, DCT coefficients as LINESTRINGZM. |
| Dependencies | None |
| SP Calls | `create_physicality` |
| Volume | Proportional to image count |
| Complexity | O(P) — per pixel region |

Extracts structural features (histogram of oriented gradients, DCT coefficients) from image regions. Results stored as LINESTRINGZM physicalities for Fréchet comparison.

### 2. Spatial Decomposition

| Field | Value |
|-------|-------|
| PassId | `image.spatial_decomposition` |
| Input | Image composition entities |
| Output | Edges: `spatial_contains` between regions. Sequences: spatial hierarchy. |
| Dependencies | None |
| SP Calls | `batch_create_edges`, `create_sequence` |
| Volume | Proportional to image complexity |
| Complexity | O(P × log P) — quadtree decomposition |

Recursively subdivides images into spatial regions. Creates a quadtree-like hierarchy of `pixel_region` composition entities with spatial containment edges.

### 3. Color Space Analysis

| Field | Value |
|-------|-------|
| PassId | `image.color_space_analysis` |
| Input | `pixel_region` entities |
| Output | Physicalities: color histogram LINESTRINGZM (X=hue, Y=saturation, Z=value, M=count). |
| Dependencies | None |
| SP Calls | `create_physicality` |

### 4. Texture Classification

| Field | Value |
|-------|-------|
| PassId | `image.texture_classification` |
| Input | `pixel_region` entities with feature physicalities |
| Output | Junctions: classification against texture reference table. |
| Dependencies | `image.feature_extraction` |

### 5. Shape Detection

| Field | Value |
|-------|-------|
| PassId | `image.shape_detection` |
| Input | `pixel_region` entities |
| Output | Physicalities: contour LINESTRINGZM. Edges: `has_shape` to shape type entities. |
| Dependencies | None |

### 6. Pattern Recognition

| Field | Value |
|-------|-------|
| PassId | `image.pattern_recognition` |
| Input | `pixel_region` entities with feature physicalities |
| Output | Edges: `visual_similarity` between regions with similar feature profiles. |
| Dependencies | `image.feature_extraction` |

### 7. Composition Analysis

| Field | Value |
|-------|-------|
| PassId | `image.composition_analysis` |
| Input | Image composition entities |
| Output | Physicalities: visual weight distribution. Edges: `compositional_element`. |
| Dependencies | `image.spatial_decomposition` |

### 8. Perceptual Hashing

| Field | Value |
|-------|-------|
| PassId | `image.perceptual_hashing` |
| Input | Image composition entities |
| Output | Edges: `perceptually_similar` between images with similar pHash values. |
| Dependencies | None |

---

## Audio Passes (22)

### 1. Spectral Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.spectral_analysis` |
| Input | `audio_chunk` entities |
| Output | Physicalities: FFT spectrum LINESTRINGZM (X=freq bin, Y=magnitude, Z=phase, M=significance). STFT spectrogram MULTILINESTRINGZM. |
| Dependencies | None |
| SP Calls | `create_physicality` |
| Complexity | O(N log N) — FFT per chunk |

Core spectral transform. Every subsequent audio pass builds on spectral data.

### 2. Pitch Detection

| Field | Value |
|-------|-------|
| PassId | `audio.pitch_detection` |
| Input | `audio_chunk` entities with spectral physicalities |
| Output | Physicalities: pitch contour LINESTRINGZM (X=time, Y=Hz). |
| Dependencies | `audio.spectral_analysis` |

### 3. Rhythm Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.rhythm_analysis` |
| Input | `audio_chunk` entities |
| Output | Edges: `beat_grid` between tempo entities and audio chunks. Physicalities: beat pattern LINESTRINGZM. |
| Dependencies | `audio.onset_detection` |

### 4. Harmonic Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.harmonic_analysis` |
| Input | `audio_chunk` entities with spectral physicalities |
| Output | Edges: `has_chord`, `has_key`. |
| Dependencies | `audio.spectral_analysis`, `audio.pitch_detection` |

### 5. Timbre Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.timbre_analysis` |
| Input | `audio_chunk` entities |
| Output | Physicalities: MFCC frame LINESTRINGZM, spectral centroid timeseries. |
| Dependencies | `audio.spectral_analysis` |

### 6. Onset Detection

| Field | Value |
|-------|-------|
| PassId | `audio.onset_detection` |
| Input | `audio_chunk` entities with spectral physicalities |
| Output | Sequences: onset time positions. Edges: `onset_at` linking chunks to time positions. |
| Dependencies | `audio.spectral_analysis` |

### 7. Envelope Extraction

| Field | Value |
|-------|-------|
| PassId | `audio.envelope_extraction` |
| Input | `audio_chunk` entities |
| Output | Physicalities: ADSR envelope LINESTRINGZM (X=time, Y=amplitude). |
| Dependencies | `audio.onset_detection` |

### 8. Formant Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.formant_analysis` |
| Input | `audio_chunk` entities (speech) |
| Output | Physicalities: formant trajectory LINESTRINGZM (X=time, Y=F1, Z=F2, M=F3). |
| Dependencies | `audio.spectral_analysis` |

### 9. Source Separation

| Field | Value |
|-------|-------|
| PassId | `audio.source_separation` |
| Input | `audio_recording` entities |
| Output | Edges: `component_of` linking separated sources to mix. New `audio_chunk` entities per source. |
| Dependencies | `audio.spectral_analysis`, `audio.harmonic_analysis` |

### 10. Spatial Audio Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.spatial_audio_analysis` |
| Input | `audio_recording` entities (multi-channel) |
| Output | Physicalities: stereo field map. |
| Dependencies | None |

### 11. Dynamic Range Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.dynamic_range_analysis` |
| Input | `audio_chunk` entities |
| Output | Physicalities: dynamic range profile LINESTRINGZM (X=time, Y=loudness_LUFS). |
| Dependencies | None |

### 12. Noise Profiling

| Field | Value |
|-------|-------|
| PassId | `audio.noise_profiling` |
| Input | `audio_chunk` entities with spectral physicalities |
| Output | Physicalities: noise floor spectrum. |
| Dependencies | `audio.spectral_analysis` |

### 13. Transient Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.transient_analysis` |
| Input | `audio_chunk` entities |
| Output | Edges: `has_transient`. Physicalities: attack shape LINESTRINGZM. |
| Dependencies | `audio.onset_detection` |

### 14. Modulation Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.modulation_analysis` |
| Input | `audio_chunk` entities with pitch contour |
| Output | Physicalities: vibrato/tremolo rate LINESTRINGZM. |
| Dependencies | `audio.pitch_detection` |

### 15. Psychoacoustic Modeling

| Field | Value |
|-------|-------|
| PassId | `audio.psychoacoustic_modeling` |
| Input | `audio_chunk` entities with spectral physicalities |
| Output | Physicalities: masking curve, critical band energies. |
| Dependencies | `audio.spectral_analysis` |

### 16. Temporal Pattern

| Field | Value |
|-------|-------|
| PassId | `audio.temporal_pattern` |
| Input | `audio_chunk` entities |
| Output | Edges: `rhythmic_motif` between chunks with similar temporal patterns. |
| Dependencies | `audio.rhythm_analysis` |

### 17. Spectral Pattern

| Field | Value |
|-------|-------|
| PassId | `audio.spectral_pattern` |
| Input | `audio_chunk` entities with spectral physicalities |
| Output | Edges: `timbral_motif` between chunks with similar spectral shapes. |
| Dependencies | `audio.timbre_analysis` |

### 18. Cross-Modal Alignment

| Field | Value |
|-------|-------|
| PassId | `audio.cross_modal_alignment` |
| Input | `audio_chunk` + `ud_sentence` entities (speech with transcripts) |
| Output | Edges: `transcription_of` linking audio chunks to text entities. |
| Dependencies | `audio.onset_detection` |
| Note | Cross-modality — requires text entities to exist |

### 19. Microstructure Analysis

| Field | Value |
|-------|-------|
| PassId | `audio.microstructure_analysis` |
| Input | `audio_chunk` entities |
| Output | Physicalities: sample-level pattern LINESTRINGZM. |
| Dependencies | None |

### 20. Phase Coherence

| Field | Value |
|-------|-------|
| PassId | `audio.phase_coherence` |
| Input | `audio_recording` entities (multi-channel) |
| Output | Physicalities: inter-channel phase relationship. |
| Dependencies | `audio.spectral_analysis` |

### 21. Resonance Detection

| Field | Value |
|-------|-------|
| PassId | `audio.resonance_detection` |
| Input | `audio_chunk` entities with spectral physicalities |
| Output | Edges: `has_resonance`. Physicalities: resonance frequency profile. |
| Dependencies | `audio.spectral_analysis` |

### 22. Artifact Detection

| Field | Value |
|-------|-------|
| PassId | `audio.artifact_detection` |
| Input | `audio_chunk` entities |
| Output | Edges: `has_artifact` with artifact type classification. Significance entries in `source_authority` arena (artifacts lower source trust). |
| Dependencies | `audio.spectral_analysis`, `audio.dynamic_range_analysis` |

---

## Video Passes (6)

### 1. Frame Decomposition

| Field | Value |
|-------|-------|
| PassId | `video.frame_decomposition` |
| Input | `video_frame` entities |
| Output | Atom entities for keyframes. Sequences: frame ordering. |
| Dependencies | None |

### 2. Motion Estimation

| Field | Value |
|-------|-------|
| PassId | `video.motion_estimation` |
| Input | `video_frame` entities (consecutive pairs) |
| Output | Physicalities: optical flow field. Edges: `motion_vector` between frames. |
| Dependencies | `video.frame_decomposition` |

### 3. Scene Detection

| Field | Value |
|-------|-------|
| PassId | `video.scene_detection` |
| Input | `video_frame` entities |
| Output | Composition entities for shots/scenes. Edges: `shot_boundary`. |
| Dependencies | `video.frame_decomposition` |

### 4. Temporal Segmentation

| Field | Value |
|-------|-------|
| PassId | `video.temporal_segmentation` |
| Input | Video composition entities |
| Output | Sequences: temporal structure hierarchy. |
| Dependencies | `video.scene_detection` |

### 5. Audio-Visual Sync

| Field | Value |
|-------|-------|
| PassId | `video.audio_visual_sync` |
| Input | `video_frame` + `audio_chunk` entities |
| Output | Edges: `synced_to` cross-modal edges linking frames to audio chunks. |
| Dependencies | `video.frame_decomposition`, `audio.onset_detection` |
| Note | Cross-modality |

### 6. Object Tracking

| Field | Value |
|-------|-------|
| PassId | `video.object_tracking` |
| Input | `video_frame` entities |
| Output | Edges: `tracks_object` linking object entities across frames. Sequences: object trajectories. |
| Dependencies | `video.frame_decomposition` |

---

## Pass Dependency Graph Summary

```
Text:
  morphological_analysis → semantic_similarity → collocation_detection
  cross_lingual_alignment (independent)
  frequency_analysis → collocation_detection
  dependency_parsing (independent)
  etymology_tracing (independent)

Audio:
  spectral_analysis → pitch_detection → modulation_analysis
  spectral_analysis → timbre_analysis → spectral_pattern
  spectral_analysis → harmonic_analysis → source_separation
  spectral_analysis → noise_profiling
  spectral_analysis → psychoacoustic_modeling
  spectral_analysis → resonance_detection
  spectral_analysis → phase_coherence
  spectral_analysis + dynamic_range_analysis → artifact_detection
  onset_detection → envelope_extraction
  onset_detection → transient_analysis
  onset_detection → rhythm_analysis → temporal_pattern
  onset_detection → cross_modal_alignment

Image:
  feature_extraction → texture_classification
  feature_extraction → pattern_recognition
  spatial_decomposition → composition_analysis

Video:
  frame_decomposition → motion_estimation, scene_detection, object_tracking
  scene_detection → temporal_segmentation
  frame_decomposition + audio.onset_detection → audio_visual_sync
```

---

## Pass Index

| # | PassId | Modality | Dependencies | Output Type |
|---|--------|----------|-------------|-------------|
| 1 | `text.morphological_analysis` | Text | — | Edges, Junctions |
| 2 | `text.dependency_parsing` | Text | — | Physicalities |
| 3 | `text.semantic_similarity` | Text | 1 | Significance |
| 4 | `text.cross_lingual_alignment` | Text | — | Significance |
| 5 | `text.frequency_analysis` | Text | — | Significance |
| 6 | `text.collocation_detection` | Text | 5 | Edges |
| 7 | `text.etymology_tracing` | Text | — | Edges |
| 8 | `image.feature_extraction` | Image | — | Physicalities |
| 9 | `image.spatial_decomposition` | Image | — | Edges, Sequences |
| 10 | `image.color_space_analysis` | Image | — | Physicalities |
| 11 | `image.texture_classification` | Image | 8 | Junctions |
| 12 | `image.shape_detection` | Image | — | Physicalities, Edges |
| 13 | `image.pattern_recognition` | Image | 8 | Edges |
| 14 | `image.composition_analysis` | Image | 9 | Physicalities, Edges |
| 15 | `image.perceptual_hashing` | Image | — | Edges |
| 16 | `audio.spectral_analysis` | Audio | — | Physicalities |
| 17 | `audio.pitch_detection` | Audio | 16 | Physicalities |
| 18 | `audio.rhythm_analysis` | Audio | 22 | Edges, Physicalities |
| 19 | `audio.harmonic_analysis` | Audio | 16, 17 | Edges |
| 20 | `audio.timbre_analysis` | Audio | 16 | Physicalities |
| 21 | `audio.onset_detection` | Audio | 16 | Sequences, Edges |
| 22 | `audio.envelope_extraction` | Audio | 21 | Physicalities |
| 23 | `audio.formant_analysis` | Audio | 16 | Physicalities |
| 24 | `audio.source_separation` | Audio | 16, 19 | Edges, Entities |
| 25 | `audio.spatial_audio_analysis` | Audio | — | Physicalities |
| 26 | `audio.dynamic_range_analysis` | Audio | — | Physicalities |
| 27 | `audio.noise_profiling` | Audio | 16 | Physicalities |
| 28 | `audio.transient_analysis` | Audio | 21 | Edges, Physicalities |
| 29 | `audio.modulation_analysis` | Audio | 17 | Physicalities |
| 30 | `audio.psychoacoustic_modeling` | Audio | 16 | Physicalities |
| 31 | `audio.temporal_pattern` | Audio | 18 | Edges |
| 32 | `audio.spectral_pattern` | Audio | 20 | Edges |
| 33 | `audio.cross_modal_alignment` | Audio | 21 | Edges |
| 34 | `audio.microstructure_analysis` | Audio | — | Physicalities |
| 35 | `audio.phase_coherence` | Audio | 16 | Physicalities |
| 36 | `audio.resonance_detection` | Audio | 16 | Edges, Physicalities |
| 37 | `audio.artifact_detection` | Audio | 16, 26 | Edges, Significance |
| 38 | `video.frame_decomposition` | Video | — | Entities, Sequences |
| 39 | `video.motion_estimation` | Video | 38 | Physicalities, Edges |
| 40 | `video.scene_detection` | Video | 38 | Entities, Edges |
| 41 | `video.temporal_segmentation` | Video | 40 | Sequences |
| 42 | `video.audio_visual_sync` | Video | 38, 21 | Edges |
| 43 | `video.object_tracking` | Video | 38 | Edges, Sequences |
