# Video Modality Decomposer Specification

## Identity

- **Decomposer class**: `VideoDecomposer` extends `BaseDecomposer`
- **Purpose**: Decompose video into the substrate. Composed from `ImageDecomposer` + `AudioDecomposer` + temporal analysis. NOT a monolithic implementation.
- **Dependency**: `ImageDecomposer` and `AudioDecomposer` must exist. Type system with video-specific types registered.

## Composition Architecture

The `VideoDecomposer` does NOT reimplement image or audio decomposition. It composes existing decomposers:

```
VideoDecomposer
  ├── ImageDecomposer (per frame)
  ├── AudioDecomposer (per audio track)
  └── TemporalAligner (analysis pass, video-specific)
```

Only the temporal alignment and video-specific structural analysis are new. Everything else is reused.

## Decomposition Pipeline

### Level 0: Demux Container

1. Detect container format (MP4, MKV, AVI, WebM, MOV, etc.).
2. Demux into component streams:
   - Video stream(s): sequence of frames with timestamps, codec info (H.264, H.265, VP9, AV1, etc.), frame rate, resolution.
   - Audio stream(s): raw audio data with timestamps, codec info, sample rate, channels.
   - Subtitle stream(s): if present, decomposed by `TextDecomposer`.
   - Metadata: container-level metadata (title, author, duration, etc.) as edges.
3. Decode video stream to raw frames (pixel arrays).
4. Pass audio streams to `AudioDecomposer`.

### Level 1: Frame Decomposition (via ImageDecomposer)

Each frame is passed to `ImageDecomposer.decompose()`. The image decomposer handles:
- Pixel value compositions with cascade compression.
- Spatial structure (patches, regions, contours).
- Color space decomposition.
- All image analysis passes (edges, textures, HOG, DCT, etc.).

Each frame entity has:
- An edge to the video entity: `frame_of(frame, video)`.
- An edge with timestamp: `at_time(frame, timestamp_entity)`.
- An edge with frame index: `at_position(frame, index_entity)`.

### Level 2: Audio Decomposition (via AudioDecomposer)

Each audio track is passed to `AudioDecomposer.decompose()`. The audio decomposer handles:
- Waveform as LinestringZM.
- All spectral/temporal analysis passes (FFT, MFCC, pitch, onsets, etc.).

Each audio track entity has an edge to the video entity.

### Level 3: Temporal Structure (video-specific)

- **Frame sequence**: the video entity's sequence references frame entities in order with frame rate metadata.
- **I/P/B frame typing**: each frame gets an edge for its frame type (I-frame = keyframe, P-frame = predicted, B-frame = bidirectional). This is structural metadata from the codec.
- **GOP (Group of Pictures) structure**: GOP boundaries as typed structural entities.

### Level 4: Video-Specific Analysis Passes

- `SceneChangeDetectionPass` -- identify scene boundaries (significant visual change between consecutive frames). Each scene boundary is a typed event entity. Scene segments are composition entities grouping frames.
- `MotionVectorPass` -- optical flow / motion vectors between consecutive frames. Each motion field stored as LinestringZM (X=source_x, Y=source_y, Z=displacement_magnitude, M=displacement_direction). Motion vectors characterize camera movement, object movement, scene dynamics.
- `TemporalCoherencePass` -- measure visual similarity between consecutive frames. Highly coherent regions (static background) compress well; dynamic regions (moving objects) have distinct entities.
- `ShotBoundaryPass` -- identify shot types (cut, dissolve, wipe, fade) at scene boundaries. Each shot transition is a typed entity.
- `AudioVisualAlignmentPass` -- correlate audio events (onsets, speech, music) with visual events (scene changes, mouth movements). Each alignment is a cross-modal edge.
- `OpticalFlowSummaryPass` -- aggregate motion statistics per scene: camera pan/tilt/zoom, dominant motion direction, motion energy.

### Level 5: Cross-Modal Temporal Alignment

Audio and visual streams are temporally aligned:
- Audio onsets <-> visual events at corresponding timestamps.
- Speech segments <-> speaker face regions (if detectable from model-derived visual features).
- Music beats <-> visual cuts (common in edited video).
- Subtitle timestamps <-> audio speech segments <-> visual lip movements.

Each alignment is a cross-modal edge.

### Level 6: Physicality

- Frame entities: inherit image physicality from `ImageDecomposer`.
- Audio entities: inherit audio physicality from `AudioDecomposer`.
- Scene entities: centroid derived from constituent frame centroids.
- Motion vectors: LinestringZM in frame-space.
- Video entity: top-level centroid derived from scene centroids.

## Cascade Compression

Video compresses extremely well in the substrate:
- Static backgrounds: identical across hundreds of frames. One pixel region entity referenced by frame count.
- Repeated visual patterns (logos, UI elements, static text): one composition referenced many times.
- Silence in audio track: one silence entity with duration.
- I-frames share content with surrounding P/B-frames: the delta (motion vectors) is what's stored for predicted frames, not redundant full-frame data.

## Round-Trip

`VideoRecomposer`:
1. Read container format metadata.
2. Walk frame sequence, reconstruct each frame via `ImageRecomposer`.
3. Walk audio tracks, reconstruct each via `AudioRecomposer`.
4. Mux into target container format with original timestamps.
5. For lossless frame sequences: bit-perfect. For compressed formats: the decomposer structurally decomposes the container and codec — container metadata, codec parameters, keyframe/prediction structure, motion vectors, quantized coefficients — into typed entities. Round-trip means the recomposer reconstructs a valid container from the structural entity tree. No binary blobs stored.
