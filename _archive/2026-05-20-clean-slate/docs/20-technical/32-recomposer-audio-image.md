# Audio and Image Recomposers

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing audio and image recompose, anyone designing recipes that produce media output, anyone debugging round-trip media fidelity.

---

## Audio Recomposer

### What it does

The audio recomposer takes substrate-stored audio compositions (audio_recording, audio_chunk) and produces material audio file output (WAV, FLAC, MP3, OGG/Opus). It is the inverse of the audio decomposer (`20-technical/05-modality-decomposers.md`).

### Inputs

- An audio composition entity ID (audio_recording or a sequence of audio_chunks).
- A recipe specifying:
  - Output format: `wav` (default lossless), `flac` (lossless compressed), `mp3` (lossy), `ogg_opus` (lossy modern), `pcm_raw`.
  - Sample rate (default: source rate).
  - Channels (default: source channels).
  - Bit depth for PCM-based outputs.
  - Bit rate for compressed outputs (e.g., MP3 at 128, 192, 320 kbps).
  - Re-sampling policy if target rate differs from source.
  - Loudness normalization (off, EBU R128, peak normalize).

### Pipeline

1. **Composition traversal.** Walk `composed_of_audio_chunk` from the audio_recording root in chunk-ordinal order.
2. **Sample atom dereference.** For each chunk, walk `composed_of_audio_sample` to retrieve raw PCM byte chunks.
3. **Concatenate.** Concatenate the PCM bytes in order, producing the full audio waveform.
4. **Resampling.** If the recipe's target sample rate differs, apply a high-quality resampler (substrate uses `libsoxr` for resampling, exposed as a substrate-internal C function).
5. **Channel mapping.** If channels differ (e.g., source mono → output stereo), apply the channel-mapping policy.
6. **Loudness normalization.** Apply if requested. Default off — preserve source levels.
7. **Encode.** Apply the target encoder:
   - WAV/PCM: write RIFF header + PCM samples.
   - FLAC: encode via `libflac`.
   - MP3: encode via `lame` or `libmp3lame`.
   - OGG/Opus: encode via `libopus`.
8. **Write file.** Emit the encoded byte stream.

### Round-trip fidelity

For lossless source (WAV, FLAC) decomposed and recomposed to the same lossless format with same sample rate and channels: byte-equivalent up to encoder determinism (FLAC encoders may produce different byte streams for the same PCM input due to compression heuristics; the substrate uses a deterministic FLAC encoding configuration to ensure round-trip bit-equivalence at the PCM level).

For lossy source (MP3, OGG) decomposed: the substrate stores the decoded PCM, not the lossy bytes. Recomposing back to the same lossy format re-encodes; the result is NOT byte-equivalent to the source (re-encoding introduces additional loss). For exact-byte preservation of a lossy source, ingest with `preserve_compressed_payload: true` recipe flag, which stores the lossy bytes as a separate atom alongside the decoded PCM.

### Cross-modal recompose

When the audio composition has linked transcription compositions (`audio_transcribes_to_sentence` edges), the recipe can request inline subtitle output:

```jsonc
{
  "kind": "recompose",
  "target_format": "wav",
  "subtitle_sidecar": "srt",
  "subtitle_language": "en"
}
```

This produces both the audio file and a `.srt` subtitle file with timestamps from the chunk start/end offsets and text from the transcription compositions.

### Multi-track support

Multi-track recordings (separate vocal/instrumental, multi-speaker recordings, etc.) are stored as multiple parallel audio_recording compositions linked via `parallel_track` edges. The recipe specifies which track(s) to recompose:

```jsonc
{
  "tracks": ["vocal", "instrumental"],
  "mix_policy": "stereo_separate"  // or "mono_mix", "vocal_only"
}
```

### Performance

- WAV recompose: bandwidth-bound; ~500 MB/sec.
- FLAC encode: ~10-50x realtime on modern hardware.
- MP3 encode: ~20-100x realtime.
- Opus encode: ~30-80x realtime.

For a typical 5-minute song at 44.1 kHz / 16-bit / stereo (~50 MB PCM), recompose to FLAC takes ~1-2 seconds; to MP3 192 kbps takes ~3-5 seconds.

---

## Image Recomposer

### What it does

The image recomposer takes substrate-stored image compositions (pixel_region tiles tiled into a parent image) and produces material image file output (PNG, JPEG, WebP, AVIF, raw PPM).

### Inputs

- An image composition entity ID (an image_root with composed_of_pixel_region children, or a single pixel_region).
- A recipe specifying:
  - Output format: `png` (lossless default), `jpeg` (lossy), `webp` (lossless or lossy), `avif` (modern, very efficient), `tiff`, `ppm` (debug raw).
  - Color space (default: source).
  - Bit depth (default: source).
  - Quality factor for lossy formats.
  - Output dimensions (default: source; if different, applies resampling).
  - Resampling algorithm (lanczos, bicubic, nearest).
  - ICC profile embedding (default: embed if source has one).
  - Metadata preservation (EXIF, XMP, etc. — preserved by default if stored as substrate metadata).

### Pipeline

1. **Composition traversal.** Walk `composed_of_pixel_region` from the image_root in tile-ordinal order.
2. **Pixel atom dereference.** For each tile, walk `composed_of_pixel_value` to retrieve raw pixel byte chunks.
3. **Tile reassembly.** Place each tile at its (tile_x, tile_y) position in a full-image buffer.
4. **Color space conversion.** If recipe's target color space differs from source, apply ICC-based or matrix-based conversion.
5. **Resampling.** If target dimensions differ, apply the resampling algorithm.
6. **Encode.** Apply the target encoder:
   - PNG: encode via `libpng` with deterministic flag set.
   - JPEG: encode via `libjpeg-turbo`.
   - WebP: encode via `libwebp`.
   - AVIF: encode via `libaom-av1` or `libdav1d`.
   - TIFF: encode via `libtiff`.
   - PPM: write raw RGB header + pixel bytes.
7. **Embed metadata.** If preserving EXIF/XMP, embed via the encoder's metadata API.
8. **Write file.**

### Round-trip fidelity

For lossless source (PNG, lossless WebP, TIFF) decomposed and recomposed to the same format: byte-equivalent at the pixel level. PNG encoders may produce different byte streams for the same pixel input due to filtering heuristics; the substrate uses deterministic PNG encoding parameters.

For lossy source (JPEG, lossy WebP) decomposed: substrate stores decoded pixels. Re-encoding to lossy format introduces additional loss; for exact-byte preservation, ingest with `preserve_compressed_payload: true`.

### Tile boundary handling

The substrate's image decomposer uses overlapping tiles (default 64×64 with 8-pixel overlap) to ensure pixel atoms have boundary continuity. The recomposer:

1. Reconstructs full-image pixels from non-overlapping core regions.
2. For overlap regions: blends from both contributing tiles using the substrate's deterministic blending algorithm (typically: weighted average with hat-function weighting that goes to zero at tile edges).

This produces a seamless reconstruction even when individual tile compositions have been refined or modified.

### Cross-modal recompose

When the image has linked text compositions (`has_caption`, `region_depicts_object`, etc.), the recipe can request annotation overlay:

```jsonc
{
  "kind": "recompose",
  "target_format": "png",
  "overlays": {
    "captions": true,
    "object_bounding_boxes": true,
    "annotation_color": "yellow",
    "annotation_font": "DejaVu Sans"
  }
}
```

This produces an image with overlaid annotations rendered via the substrate's basic 2D rendering primitives. For complex visualization, the recipe can produce a sidecar JSON describing annotations and let the consumer render them.

### Multi-image / video frame recompose

For video, each frame is an image composition. The video recomposer composes:
- Per-frame image recompose.
- Audio recompose (if the video has an audio track).
- Container packaging (MP4, WebM, MKV) via `ffmpeg` invocation.

Video recompose is treated as a sibling pipeline that calls into both audio and image recomposers, rather than as a separate recomposer per se.

### Performance

- PNG encode: ~100-300 MB/sec (compression overhead).
- JPEG encode: ~500-1000 MB/sec.
- WebP encode: ~200-500 MB/sec.
- AVIF encode: ~10-50 MB/sec (much slower; trade for compression efficiency).
- Tile reassembly: bandwidth-bound; ~1 GB/sec.

For a typical 1920×1080 RGBA8 image (~8 MB raw), recompose to PNG takes ~30-100 ms; to JPEG ~10-30 ms; to AVIF ~200-1000 ms.

---

## Common conventions

### Recipe-driven format selection

Both recomposers accept a recipe with the same general shape:

```jsonc
{
  "kind": "recompose",
  "target_format": "<format_code>",
  "output_path": "/path/to/output.ext",
  "metadata_sidecar": true,
  "audit_trace_emit": true
}
```

The `metadata_sidecar` produces a JSON file alongside the output documenting the recompose pass.

### Audit trace

Every recompose emits an `audit_trace` entity:
- Source composition ID.
- Recipe used.
- Output bytes' SHA-256 hash.
- Started_at, completed_at.
- Recompose run ID.

Tenants can verify that a recomposed file is what was expected by comparing the SHA-256 against the audit trace's record.

### Provenance preservation

The substrate-stored compositions have provenance edges. The recomposer's metadata sidecar surfaces this provenance:

```json
{
  "audit_trace_id": "...",
  "source_composition_id": "...",
  "provenance_summary": [
    {"class": "public.seed.openimages_v7", "source": "Open Images v7 / 2025-09 dump"},
    {"class": "private.tenant:acme-corp.ingest", "source": "ACME internal scan / Q1 2026"}
  ]
}
```

This is how customers know who put the data in the substrate that produced their export — Substrate Law 8 (provenance preservation) extends through the recompose boundary.

### Failure modes

Per Substrate Law 13 (fail loud):

- Missing atoms: recompose fails immediately with diagnostic.
- Format unsupported: recipe rejected at parse time.
- Disk full: hard fail; partial outputs cleaned up.
- Encoder error: surfaced with encoder diagnostic; not silently swallowed.

## Cross-references

- Audio decomposer (the inverse): `20-technical/05-modality-decomposers.md` (Audio section)
- Image decomposer (the inverse): `20-technical/05-modality-decomposers.md` (Image section)
- Recomposer contract (general principles): `10-architecture/06-recomposer-contract.md`
- Cognitive functions: `20-technical/08-cognitive-functions.md`
- Substrate Law 7 (Unicode for text; analogous principles for binary modalities — preserve standard formats): `10-architecture/01-substrate-laws.md`
- Substrate Law 13 (fail loud): `10-architecture/01-substrate-laws.md`

## External references

- libflac: <https://xiph.org/flac/>
- libopus: <https://opus-codec.org/>
- libpng: <http://www.libpng.org/>
- libjpeg-turbo: <https://libjpeg-turbo.org/>
- libwebp: <https://developers.google.com/speed/webp>
- libaom (AVIF encoding): <https://aomedia.googlesource.com/aom/>
- libsoxr (audio resampling): <https://sourceforge.net/projects/soxr/>
