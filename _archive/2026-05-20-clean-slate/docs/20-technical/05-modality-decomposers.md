# Modality Decomposers — Image, Audio, Video

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers ingesting non-text content into the substrate; anyone debugging cross-modal queries; authors of new modality-format support.

---

## What modality decomposers are

Modality decomposers ingest binary content (images, audio waveforms, video streams) and produce substrate state representing the content as compositions with format-aware physicality. They produce the SAME substrate-shaped output (typed compositions with linestring4d trajectories, edges with provenance, etc.) as text and code decomposers — the only difference is the parsing front-end is binary-format-aware (Kaitai Struct grammars or hand-written readers), not text-format-aware (tree-sitter).

Like the code decomposer, modality decomposers are NOT structurally separate from the text decomposer. They produce substrate state that can be cross-referenced with text/code via cross-modal edges (e.g., `has_caption`, `recording_of`, `depicts`).

## The three primary modality decomposers

This document specifies three:
- **ImageDecomposer**: PNG, JPEG, WebP, BMP, TIFF, GIF
- **AudioDecomposer**: WAV, FLAC, MP3, OGG-Vorbis, OGG-Opus
- **VideoDecomposer**: MP4, WebM (composes ImageDecomposer + AudioDecomposer per frame/track)

Other modality decomposers (3D model formats, music notation, MIDI, biological data, etc.) are extension targets — same contract, different binary readers.

## Common architecture

All modality decomposers share a pipeline shape:

```
input bytes (binary)
        │
        ▼
[1] Format detection (magic bytes, declared MIME type, file extension)
        │
        ▼
[2] Container parsing (extract metadata + raw payloads via Kaitai grammar or libformat)
        │
        ▼
[3] Per-payload decomposition into typed compositions
        │
        ▼
[4] Cross-payload edges (e.g., audio/visual sync in video)
        │
        ▼
[5] File-level wrapper composition with metadata edges
        │
        ▼
returns: BLAKE3 hash of root composition
```

Format-specific differences are in steps 2 and 3. The substrate shape and contract are identical across modalities.

---

## ImageDecomposer

### What it is

Ingests raster image files (PNG, JPEG, WebP, BMP, TIFF, GIF). For each image, produces a substrate composition representing the image structure (header → metadata → pixel grid → optional regions). The pixel grid itself is a composition of pixel-region entities; for very large images, pixel data is stored as physicality (not per-pixel substrate entities) to keep edge counts manageable.

### Atom model for images (per ADR-002 / multi-atom vocabulary)

The substrate admits `pixel_value` as an atom type for images. A pixel value is an RGBA tuple `(r, g, b, a)` of four uint8 values. Atom hash: `BLAKE3(le8(r) || le8(g) || le8(b) || le8(a))`. Identical pixel values across all images converge to the same atom row.

For grayscale images: pixel atoms have `(g, g, g, 255)` — same atom for grayscale RGB triples.

For HDR / 16-bit / floating-point images: separate atom types `pixel_value_uint16`, `pixel_value_float32` with appropriate canonical encoding.

### Composition tiers

```
codepoint atom (text characters in metadata fields)
pixel_value atom (color values in pixel grid)
        │
        ▼
pixel_region (rectangular tile of pixel values; configurable size)
        │
        ▼
image (collection of pixel_region tiles + metadata)
        │
        ▼
image_file (image + format header + EXIF metadata + ICC profile + ...)
```

Tiles are introduced because per-pixel substrate entities at full HD resolution would produce ~2M entities per image. Tile-based decomposition (e.g., 8×8 or 16×16 pixel tiles) reduces per-image entity count by 64–256× while preserving content-addressed convergence — identical tiles across images share rows.

### The pipeline

1. **Format detection**: magic bytes (`89 50 4E 47` for PNG, `FF D8 FF` for JPEG, `52 49 46 46` for WebP/RIFF, etc.) plus extension hint.
2. **Container parsing**: Kaitai Struct grammar (or libpng/libjpeg via FFI) to extract:
   - Width, height, color depth, color space
   - Compression info
   - EXIF metadata (camera, GPS, timestamps) — text fields go through text_decompose
   - ICC color profile
   - Any embedded text annotations
3. **Pixel grid extraction**: decode compressed pixel data to raw RGBA buffer.
4. **Tile decomposition**: chunk into N×N pixel tiles (default 16×16). For each tile:
   - Build linestring4d through pixel S³-projected colors (HSV-derived 4D position with H, S, V, A as the four axes).
   - Compose tile via Merkle of pixel-value atoms.
   - Upsert as `pixel_region` entity.
5. **Image composition**: assemble linestring4d through tile centroids.
6. **Image_file wrapper**: attach metadata edges.

### Cross-modal edges

After image ingestion, customer code (or downstream decomposers like Visual Genome) can attach:
- `has_caption` from image_file to text_composition
- `depicts` from pixel_region to concept entity (Visual Genome scene graph pattern)
- `has_visual_attribute` for color/shape/texture properties
- `cooccurs_visually` between regions in the same image

The substrate's geometry layer enables visual similarity queries via 4D Fréchet over image trajectories.

### SQL surface

```sql
hartonomous.image_decompose(
    image_path     TEXT,
    provenance_id  INT,
    options        JSONB  DEFAULT '{}'::jsonb     -- {tile_size: 16, decode_metadata: true, ...}
) RETURNS BYTEA;
```

### Performance characteristics

| Image | Latency target |
|---|---|
| Small (256×256, ~50KB) | ~10–20 ms |
| HD (1920×1080, ~500KB-2MB) | ~100–500 ms |
| 4K (3840×2160, ~5–20MB) | ~500 ms – 2s |

Bottleneck: pixel decoding + tile composition. Convergence (identical tiles dedup'd) is substantial for synthetic/cartoon images, modest for photographs.

### Validation gates

- D-image-roundtrip: decompose → reconstruct from substrate → compare bytes (lossy formats accept structural equality, not byte equality)
- D-tile-convergence: same image ingested twice produces identical entity rows (no duplicates)
- D-shared-tile: two images with identical regions share the same `pixel_region` entities

### Failure modes

- `image_format_unsupported`: format not in registered readers
- `image_corrupt`: container parsing fails
- `image_too_large`: exceeds substrate's per-call ingest cap (configurable)

---

## AudioDecomposer

### What it is

Ingests audio waveforms (WAV, FLAC, MP3, OGG-Vorbis, OGG-Opus). Produces substrate state representing the audio's container metadata and waveform structure. The waveform itself is stored as physicality (LINESTRINGZ in PostGIS GeometryZM, where X=time, Y=amplitude, Z=frequency-band-or-channel) rather than per-sample entities — sample-level granularity would explode entity counts.

### Atom model for audio

The substrate admits `audio_sample` as an atom type for cases where sample-level identity matters (e.g., comparing exact samples between recordings of the same speech). Atom hash: `BLAKE3(le32(sample_value) || le32(sample_index_within_chunk))`. Used sparingly; most audio ingestion uses physicality-only representation for efficiency.

### Composition tiers

```
audio_sample atom (when needed)
        │
        ▼
audio_chunk (e.g., 1-second windows; physicality stored as LINESTRINGZ)
        │
        ▼
audio_recording (collection of chunks + container metadata)
        │
        ▼
audio_file (recording + format header + ID3/Vorbis comments + ...)
```

Chunk size is configurable; default 1 second × sample_rate. Identical chunks across recordings converge.

### The pipeline

1. **Format detection**: magic bytes per format.
2. **Container parsing**: WAV's RIFF/fmt/data chunks (Kaitai Struct), FLAC's metadata blocks + frames, MP3's ID3 + MPEG frames, OGG's pages, etc. Extract:
   - Sample rate, bit depth, channel count
   - Codec/encoding info
   - ID3 / Vorbis comment metadata (title, artist, etc.) — text through text_decompose
2. **Waveform extraction**: decode compressed audio (libsndfile, libFLAC, libmp3lame, libvorbis) to raw PCM.
3. **Chunking**: split PCM into ~1-second chunks. Per chunk:
   - Build LINESTRINGZ where vertices = (sample_index, sample_value, channel_id) — preserves time × amplitude × channel structure.
   - Optionally compute FFT/spectral features per chunk and store as additional physicality of type `fft_spectrum` (LINESTRINGZ where Y=magnitude, Z=phase).
   - Optionally compute MFCC / chromagram / pitch contour for higher-level analysis.
4. **Recording composition**: assemble chunk centroids.
5. **Audio_file wrapper**: attach format/metadata edges.

### Cross-modal edges

- `recording_of` from audio_recording to text_composition (Tatoeba pattern, where audio is paired with sentence text)
- `transcribes_to` from audio_recording to text_composition (transcript from ASR)
- `has_speaker` to a speaker entity
- `cooccurs_audially` between chunks in the same recording

### SQL surface

```sql
hartonomous.audio_decompose(
    audio_path     TEXT,
    provenance_id  INT,
    options        JSONB  DEFAULT '{}'::jsonb
) RETURNS BYTEA;
```

### Performance characteristics

| Audio | Latency target |
|---|---|
| 5-second clip (~100KB MP3) | ~100–300 ms |
| 1-minute clip (~1–3MB) | ~1–3 s |
| 1-hour audiobook | ~30–60 s |

Bottleneck: codec decode + chunk-level physicality emission.

---

## VideoDecomposer

### What it is

Ingests video files (MP4, WebM). Composes ImageDecomposer (per video frame) + AudioDecomposer (per audio track) + temporal alignment edges. The video's primary identity is the composition of (frame sequence, audio track sequence, container metadata).

### Atom model for video

No new atom types. Video reuses `pixel_value` (for frame pixels) and `audio_sample` (for audio track samples) atoms. Frame-level entities are `image` compositions; per-track audio is `audio_recording`.

### Composition tiers

```
image (per frame, as ImageDecomposer produces)
audio_recording (per audio track)
        │
        ▼
video_segment (~1 second of synchronized frames + audio)
        │
        ▼
video (collection of segments + container metadata)
        │
        ▼
video_file (video + format header + subtitle tracks + metadata)
```

### The pipeline

1. **Container parsing**: MP4 box hierarchy (ftyp, moov, mvhd, trak, mdia, etc.) via Kaitai. WebM's EBML structure similarly. Extract per-track metadata (codec, framerate, sample rate, language, etc.).
2. **Frame extraction** (typically via ffmpeg-FFI): per-frame raw RGB buffer at original resolution and framerate. For substrate efficiency, configurable frame-skip (e.g., decompose every Nth frame, not every frame, when full temporal granularity isn't needed).
3. **Per-frame ImageDecomposer**: each extracted frame goes through the image pipeline producing an `image` entity.
4. **Audio track extraction**: each audio track decoded; AudioDecomposer produces `audio_recording` entities per track.
5. **Subtitle track extraction**: WebVTT / SRT / TTML subtitle text through text_decompose.
6. **Temporal alignment**: per video_segment (~1-second window), build linestring4d through frame centroids; attach audio chunk centroid via `cooccurs_temporally` edge.
7. **Video composition**: assemble segments.
8. **Video_file wrapper**: container metadata.

### Cross-modal edges

- `has_audio_track` from video_file to audio_recording
- `has_subtitle_track` from video_file to text_composition
- `cooccurs_temporally` linking frame and audio chunk at same timestamp
- `depicts_action` from video_segment to action concept entity

### SQL surface

```sql
hartonomous.video_decompose(
    video_path     TEXT,
    provenance_id  INT,
    options        JSONB  DEFAULT '{}'::jsonb     -- {frame_skip: 5, max_frames: 1000, ...}
) RETURNS BYTEA;
```

### Performance characteristics

| Video | Latency target |
|---|---|
| Short clip (10s, 720p, ~10MB) | ~5–20 s |
| Music video (4 min, 1080p, ~100MB) | ~1–5 min |
| Feature-length film (2 hr, 1080p, ~5GB) | ~1–2 hr |

Video decomposition is the most expensive modality due to frame-by-frame work. Configurable frame-skip is essential for production-scale ingestion. Substrate operators typically choose frame-skip values per use case (every frame for action recognition; every 30th frame for content summarization).

### Validation gates

- D-video-frame-count: extracted frame count matches container's declared frame count (modulo frame-skip)
- D-video-audio-sync: audio chunks and frames at the same timestamp link via `cooccurs_temporally`
- D-video-roundtrip: structurally valid output reconstructed from substrate

---

## Cross-modal queries

After modality ingestion plus seed-source cross-modal alignment data (Tatoeba audio-text, Visual Genome image-caption, etc.), substrate enables:

```sql
-- Find audio recordings of a sentence:
SELECT a.entity_hash, a.duration_ms
FROM substrate.entity a
JOIN substrate.edge e ON e.participants @> ARRAY[a.entity_hash]
JOIN ref.edge_type et ON et.id = e.edge_type_id
WHERE et.code = 'recording_of'
  AND e.participants @> ARRAY[$sentence_hash];

-- Find images depicting a concept:
SELECT i.entity_hash
FROM substrate.entity i
JOIN substrate.edge e ON ...
WHERE et.code = 'depicts'
  AND target = $concept_hash;

-- Similar audio waveforms (4D Fréchet over audio_chunk linestring physicality):
SELECT entity_hash, hartonomous.geometric.frechet_z(...) AS dist
FROM substrate.physicality
WHERE physicality_type = 'audio_chunk'
ORDER BY dist ASC LIMIT 10;
```

## Extension to new modality formats

Adding support for a new format (e.g., 3D point clouds, MIDI, biological FASTA):

1. Define atom types if needed (new atom in `ref.entity_type`).
2. Author a Kaitai Struct grammar (or use libformat via FFI) for binary parsing.
3. Define composition tiers and physicality types.
4. Implement decomposer following the common-architecture pipeline shape.
5. Define cross-modal edge types (how this modality relates to others).
6. Add validation gates.
7. Document in this file.

## Common failure modes

All modality decomposers share:
- `format_unsupported`: format not in registered readers
- `format_corrupt`: container fails parse
- `decode_failure`: codec failure mid-decompression
- `exceeds_size_limit`: file exceeds per-call cap

## Cross-references

- Decomposer contract: `10-architecture/05-decomposer-contract.md`
- Tree-sitter strategy (binary-format complement via Kaitai): `20-technical/16-tree-sitter-grammar-strategy.md`
- Text decomposer (called for embedded text fields): `20-technical/02-text-decomposer.md`
- Geometry (4D physicality): `10-architecture/03-geometry-4d.md`
- Cross-modal edge types: `20-technical/11-edge-types-catalog.md`
- ADR-002 (multi-atom vocabulary): `60-status/04-decisions-log.md`

## External references

- Kaitai Struct: <https://kaitai.io/>
- libsndfile (audio): <http://www.mega-nerd.com/libsndfile/>
- ffmpeg (video): <https://ffmpeg.org/>
- PNG specification: <https://www.w3.org/TR/png/>
- JPEG specification: ITU-T T.81
- MP4 specification: ISO/IEC 14496-12
- WebVTT: <https://www.w3.org/TR/webvtt1/>
