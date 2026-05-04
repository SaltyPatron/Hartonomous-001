using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Recomposers;

/// <summary>
/// Writes a <see cref="SafetensorsFile"/> to a <see cref="Stream"/> in the
/// safetensors binary wire format per
/// docs/specs/csharp/recomposers.md § "Streaming variant" and the upstream
/// safetensors spec:
///   1. Build JSON header with per-tensor (dtype, shape, data_offsets [begin,end])
///      ordered to match the data block layout.
///   2. Serialize header to UTF-8 bytes; pad to 8-byte alignment with spaces.
///   3. Write 8-byte little-endian uint64 of header byte length.
///   4. Write header bytes.
///   5. Write tensor data blocks contiguously in the same order.
///
/// This is the deterministic container layer for any synthesized
/// SafetensorsFile payload — distillation (the synthesis itself) is the
/// recomposer's responsibility; this writer is purely the wire encoding.
/// </summary>
public static class SafetensorsWriter
{
    public static Task WriteAsync(SafetensorsFile file, Stream output, CancellationToken ct)
        => WriteAsync(file, output, auditMetadata: null, ct);

    /// <summary>
    /// Writes a safetensors file with optional audit-chain metadata embedded
    /// in the <c>__metadata__</c> block. <paramref name="auditMetadata"/>
    /// keys (e.g., <c>hartonomous_substrate_state</c>,
    /// <c>hartonomous_recipe_id</c>, <c>hartonomous_recomposer_version</c>,
    /// <c>hartonomous_provenance_chain</c>) are written verbatim alongside
    /// the existing model_name field.
    /// </summary>
    public static async Task WriteAsync(
        SafetensorsFile file,
        Stream output,
        IReadOnlyDictionary<string, string>? auditMetadata,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        // Order tensors deterministically by name so the on-disk layout is
        // reproducible across runs (Law #6).
        List<KeyValuePair<string, TensorData>> ordered = new(file.Tensors);
        ordered.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        // Compute (begin, end) byte offsets for each tensor's data region.
        long offset = 0;
        Dictionary<string, (long Begin, long End)> offsets = new(ordered.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, TensorData> kv in ordered)
        {
            int len = kv.Value.Data.Length;
            offsets[kv.Key] = (offset, offset + len);
            offset += len;
        }

        // Build the JSON header. Use UTF-8 JsonWriter so we get spec-compliant
        // formatting (no .NET-specific escapes), then pad with spaces to a
        // multiple of 8 bytes for alignment.
        byte[] headerBytes;
        using (MemoryStream headerMs = new())
        {
            JsonWriterOptions opts = new() { Indented = false, SkipValidation = false };
            using (Utf8JsonWriter writer = new(headerMs, opts))
            {
                writer.WriteStartObject();

                // __metadata__ carries the model name plus any audit-chain
                // keys the recomposer supplied (substrate state Merkle root,
                // recipe id, recomposer version, provenance chain).
                writer.WriteStartObject("__metadata__");
                writer.WriteString("model_name", file.ModelName);
                if (auditMetadata is not null)
                {
                    foreach (KeyValuePair<string, string> kv in auditMetadata)
                    {
                        writer.WriteString(kv.Key, kv.Value);
                    }
                }
                writer.WriteEndObject();

                foreach (KeyValuePair<string, TensorData> kv in ordered)
                {
                    writer.WriteStartObject(kv.Key);
                    writer.WriteString("dtype", kv.Value.Dtype);

                    writer.WriteStartArray("shape");
                    foreach (int dim in kv.Value.Shape)
                    {
                        writer.WriteNumberValue(dim);
                    }
                    writer.WriteEndArray();

                    writer.WriteStartArray("data_offsets");
                    writer.WriteNumberValue(offsets[kv.Key].Begin);
                    writer.WriteNumberValue(offsets[kv.Key].End);
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }
            headerBytes = headerMs.ToArray();
        }

        // Pad header to 8-byte alignment with ASCII spaces (safetensors
        // convention — JSON whitespace is ignored by the parser).
        int padding = (int)((8 - (headerBytes.Length % 8)) % 8);
        if (padding > 0)
        {
            byte[] padded = new byte[headerBytes.Length + padding];
            Buffer.BlockCopy(headerBytes, 0, padded, 0, headerBytes.Length);
            for (int i = 0; i < padding; i++)
            {
                padded[headerBytes.Length + i] = (byte)' ';
            }
            headerBytes = padded;
        }

        // Write header length as 8-byte little-endian uint64.
        Memory<byte> sizeBuf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBuf.Span, (ulong)headerBytes.Length);
        await output.WriteAsync(sizeBuf, ct).ConfigureAwait(false);

        // Write header bytes.
        await output.WriteAsync(headerBytes, ct).ConfigureAwait(false);

        // Write tensor data blocks in the same order as the offsets table.
        foreach (KeyValuePair<string, TensorData> kv in ordered)
        {
            await output.WriteAsync(kv.Value.Data, ct).ConfigureAwait(false);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
