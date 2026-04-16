# Recomposers

**Status**: ✅ Complete

How data gets OUT of the system. Recomposers traverse the graph and reconstitute output in a target format. Decomposition is lossy in format but lossless in content — recomposition reconstructs semantic content, not byte-for-byte source files.

---

## Architecture

```
Caller
  │
  ▼
IRecomposer<T>.RecomposeAsync(entityId, options)
  │
  ├─ BaseRecomposer<T>.GetEntityAsync(entityId)     ← fetch root entity
  ├─ BaseRecomposer<T>.GetChildrenAsync(entityId)    ← composition children
  ├─ BaseRecomposer<T>.GetEdgesAsync(entityId)       ← neighbors() function
  ├─ BaseRecomposer<T>.GetPhysicalitiesAsync(entityId)
  ├─ BaseRecomposer<T>.GetJunctionsAsync(entityId)
  │
  ├─ Recursive expansion (FlattenToAtomsAsync if depth = unbounded)
  │
  └─ Format-specific assembly ← subclass responsibility
      │
      ▼
      T (output type)
```

Every recomposer inherits `BaseRecomposer<T>` and implements `RecomposeCoreAsync`. The base class provides all graph traversal. The subclass assembles the output.

---

## Cross-Cutting Behavior

### Traversal Depth

Controlled by `RecompositionOptions.MaxDepth`:

| Value | Behavior |
|-------|----------|
| `null` | Expand to atoms (full depth) |
| `0` | Root entity only (metadata, no children) |
| `1` | Root + immediate children |
| `N` | Root + N levels of composition children |

### Partial Recomposition

Any composition entity can be the root. To recompose "one chapter of a book," pass the chapter's `entity_id` as the root. The recomposer doesn't know or care whether it's a chapter, a sentence, or an entire corpus.

### Streaming

`RecomposeToStreamAsync` writes output directly to a `Stream` without materializing the entire result in memory. Required for audio, image, video, and large text outputs. Implementation pattern:

```csharp
public override async Task RecomposeToStreamAsync(
    long entityId,
    RecompositionOptions options,
    Stream output,
    CancellationToken ct)
{
    await foreach (var chunk in TraverseAndAssembleAsync(entityId, options, ct))
    {
        await output.WriteAsync(chunk, ct);
    }
}
```

### Error Handling for Incomplete Graphs

If the graph is incomplete (missing children, broken sequences, dangling edge references):
- `RecompositionException` with `ErrorContext` identifying the missing entity/edge.
- No fallback. No placeholder insertion. No partial output.
- The caller receives the exception and decides what to do.

### Caching

No caching layer in the recomposer. PostgreSQL's shared_buffers handles hot data. The recomposer is a stateless function: `entity_id → T`.

---

## Per-Recomposer Implementation

### TextRecomposer

**Class**: `TextRecomposer : BaseRecomposer<string>`

**Output type**: `string` (plain text, UTF-8).

**Traversal strategy**:
1. Fetch root entity.
2. If root is a composition: fetch `sequence` entries ordered by `position`.
3. For each sequence member: recurse (depth-first, ordered by position).
4. At atom level: the entity's content IS the text (word form, codepoint, grapheme cluster).
5. Concatenate atoms in sequence order. Word boundaries are determined by entity type: `word_form` atoms get space-separated, `codepoint` atoms get concatenated directly.

**Round-trip fidelity**: Decompose → recompose produces semantically identical text. NOT byte-identical: original whitespace normalization, punctuation placement, and formatting are not preserved. The hash of the recomposed text will NOT match the original entity hash (the original hash is of the source, not the recomposition).

**Annotated mode**: When `RecompositionOptions.IncludeAnnotations = true`, output includes inline annotations from edges and junctions:

```
The [cat/NN/nsubj] [sat/VBD/root] on [the/DT/det] [mat/NN/obl].
```

This is returned as structured `AnnotatedText` (a companion type), not as the `string` return type. The annotated variant uses `RecomposeAsync<AnnotatedText>` — a separate generic instantiation, not a mode flag on the string recomposer.

---

### ImageRecomposer

**Class**: `ImageRecomposer : BaseRecomposer<ImageBuffer>`

**Output type**: `ImageBuffer` — a record wrapping `byte[] Pixels`, `int Width`, `int Height`, `int Channels`, `PixelFormat Format`.

**Traversal strategy**:
1. Fetch root image entity.
2. Fetch physicalities → extract `POINTZM` positions (S3 surface coordinates).
3. Fetch composition children (spatial decomposition hierarchy: image → regions → patches → pixels).
4. At leaf level: read pixel data from physicality tier values.
5. Assemble pixel buffer by placing each patch at its spatial position (derived from S3 → pixel coordinate inverse mapping).

**Streaming variant**: `RecomposeToStreamAsync` writes PNG format (header + IDAT chunks) incrementally using row-by-row scanline encoding. No dependency on System.Drawing or ImageSharp — hand-written PNG encoder (deflate + CRC32 from .NET BCL).

**Depth control**: `MaxDepth = 1` returns region-level summary (bounding boxes, dominant colors from junction data). Full depth returns pixel-accurate reconstruction.

---

### AudioRecomposer

**Class**: `AudioRecomposer : BaseRecomposer<AudioBuffer>`

**Output type**: `AudioBuffer` — a record wrapping `float[] Samples`, `int SampleRate`, `int Channels`, `int BitsPerSample`.

**Traversal strategy**:
1. Fetch root audio entity.
2. Fetch sequence entries (temporal ordering of audio chunks).
3. For each chunk (in sequence order): fetch physicality data.
4. Physicality contains spectral/waveform data stored as `LINESTRINGZM` (time-frequency representation on S3 surface).
5. Inverse transform: S3 coordinates → frequency domain → time domain (inverse FFT).
6. Concatenate PCM sample blocks in sequence order.

**Streaming variant**: `RecomposeToStreamAsync` writes WAV format (44-byte header + raw PCM data). Header written first with total size calculated from sequence count × chunk duration. Samples written as 16-bit signed integers (little-endian) or 32-bit float depending on `RecompositionOptions.BitDepth`.

**Sample-accurate reconstruction**: YES for losslessly decomposed audio (PCM sources). Lossy sources (MP3, AAC) are reconstructed from the stored spectral representation — perceptually equivalent but not bit-identical to the decoded original.

---

### VideoRecomposer

**Class**: `VideoRecomposer : BaseRecomposer<VideoFrameSequence>`

**Output type**: `VideoFrameSequence` — a record wrapping `IReadOnlyList<ImageBuffer> Frames`, `AudioBuffer Audio`, `double FrameRate`, `TimeSpan Duration`.

**Traversal strategy**:
1. Fetch root video entity.
2. Fetch sequence entries — two parallel sequences: frame sequence (image entities) and audio track (audio entity).
3. For frame sequence: delegate each frame to `ImageRecomposer.RecomposeAsync`.
4. For audio track: delegate to `AudioRecomposer.RecomposeAsync`.
5. Combine into `VideoFrameSequence`.

**Streaming variant**: `RecomposeToStreamAsync` writes raw frame sequence (Y4M format — simple uncompressed video container). Each frame is emitted as it's recomposed. No codec encoding — that's the caller's responsibility.

**Depth control**: `MaxDepth = 0` returns metadata only (frame count, duration, resolution). `MaxDepth = 1` returns thumbnail-resolution key frames.

---

### SafetensorsRecomposer

**Class**: `SafetensorsRecomposer : BaseRecomposer<SafetensorsFile>`

**Output type**: `SafetensorsFile` — a record wrapping `IReadOnlyDictionary<string, TensorData> Tensors`, `string ModelName`. `TensorData` contains `string Dtype`, `int[] Shape`, `byte[] Data`.

**Traversal strategy**:
1. Fetch root model architecture entity.
2. Fetch composition children (tensor entities) via `in_model` edges.
3. For each tensor: fetch physicality data (raw weight bytes stored as tier data).
4. Reconstruct tensor metadata from junction data (`tensor_tensor_role` for layer name, `model_architecture_class` for architecture type).
5. Assemble `SafetensorsFile` record with tensor name → data mapping.

**Streaming variant**: `RecomposeToStreamAsync` writes safetensors binary format directly:
1. Build JSON header (tensor names, dtypes, shapes, byte offsets).
2. Write 8-byte header length (little-endian u64).
3. Write JSON header (padded to 8-byte alignment).
4. Write tensor data blocks sequentially.

**Round-trip fidelity**: Bit-exact. Decompose a safetensors file → recompose → produces byte-identical output. The safetensors format is deterministic (sorted keys, no padding variance).

---

## Recomposer Index

| Recomposer | Class | Output Type | Streaming Format | Round-Trip Fidelity |
|-----------|-------|------------|-----------------|-------------------|
| Text | `TextRecomposer` | `string` | UTF-8 byte stream | Semantic (not byte-identical) |
| Image | `ImageRecomposer` | `ImageBuffer` | PNG | Pixel-accurate |
| Audio | `AudioRecomposer` | `AudioBuffer` | WAV (PCM) | Sample-accurate (lossless sources) |
| Video | `VideoRecomposer` | `VideoFrameSequence` | Y4M | Frame-accurate |
| SafeTensors | `SafetensorsRecomposer` | `SafetensorsFile` | safetensors binary | Bit-exact |
