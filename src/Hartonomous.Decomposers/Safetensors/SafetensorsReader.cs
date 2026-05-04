using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Decomposers.Safetensors.Packages;

namespace Hartonomous.Decomposers.Safetensors;

public static class SafetensorsReader
{
    // 1 MiB streaming chunk. Balances syscall overhead against CPU cache pressure.
    private const int StreamingChunkBytes = 1 << 20;

    /// <summary>
    /// Opens a Stream positioned at the tensor's first byte, returning a stream
    /// that yields exactly (info.EndByte - info.BeginByte) bytes. Routes:
    ///   donor:// FilePath → IDonorPackageReader.ReadTensorAsync via DonorReaderRegistry
    ///   anything else     → File.OpenRead(filePath) + Seek(BeginByte)
    /// The donor:// path materializes the full tensor into a MemoryStream so the
    /// existing chunked-streaming code can read it linearly. For pickle / multi-subdir
    /// donors this is the cost of leaving SafetensorsReader's static API surface
    /// intact; safetensors-format donors keep the original zero-copy file-stream path.
    /// </summary>
    private static Stream OpenTensorStream(SafetensorsTensorInfo info)
    {
        if (DonorReaderRegistry.TryParsePath(info.FilePath, out int slot, out string tensorName))
        {
            IDonorPackageReader reader = DonorReaderRegistry.Resolve(slot);
            ReadOnlyMemory<byte> bytes = reader.ReadTensorAsync(tensorName, CancellationToken.None)
                .GetAwaiter().GetResult();
            long expected = info.EndByte - info.BeginByte;
            if (bytes.Length != expected)
            {
                throw new InvalidDataException(
                    $"Donor reader returned {bytes.Length} bytes for {tensorName}; expected {expected}.");
            }
            byte[] copy = bytes.ToArray();
            return new MemoryStream(copy, writable: false);
        }
        FileStream fs = File.OpenRead(info.FilePath);
        if (info.BeginByte > 0)
        {
            fs.Seek(info.BeginByte, SeekOrigin.Begin);
        }
        return fs;
    }

    /// <summary>
    /// Stream the tensor's raw bytes into <paramref name="hasher"/> without ever
    /// holding the full tensor in memory. Updates the hasher only — caller is
    /// responsible for having already fed the canonical (dtype, shape) prefix.
    /// </summary>
    public static void StreamHash(SafetensorsTensorInfo info, Blake3Hasher hasher)
    {
        long totalBytes = info.EndByte - info.BeginByte;
        if (totalBytes < 0)
        {
            throw new InvalidDataException($"Tensor {info.Name} has negative byte span {totalBytes}");
        }

        using Stream fs = OpenTensorStream(info);

        byte[] buf = new byte[StreamingChunkBytes];
        long remaining = totalBytes;
        while (remaining > 0)
        {
            int want = (int)Math.Min(remaining, StreamingChunkBytes);
            fs.ReadExactly(buf, 0, want);
            hasher.Update(buf.AsSpan(0, want));
            remaining -= want;
        }
    }

    /// <summary>
    /// Single-pass: stream tensor bytes into <paramref name="hasher"/> and decode
    /// the same bytes to <paramref name="result"/> as f64. Halves I/O and buffer
    /// allocation for Track 1 tensors (which need both the content hash and the
    /// decoded doubles). Caller feeds the canonical prefix into the hasher first.
    /// </summary>
    public static void StreamHashAndDecode(
        SafetensorsTensorInfo info, Blake3Hasher hasher, double[] result)
    {
        long numElements = info.ElementCount;
        if (numElements > int.MaxValue || numElements > result.Length)
        {
            throw new NotSupportedException(
                $"Tensor {info.Name} has {numElements} elements — exceeds decode buffer");
        }
        int bpe = info.BytesPerElement;
        long totalBytes = info.EndByte - info.BeginByte;
        if (totalBytes != numElements * bpe)
        {
            throw new InvalidDataException(
                $"Tensor {info.Name} size mismatch: {totalBytes} bytes vs expected {numElements * bpe}");
        }

        using Stream fs = OpenTensorStream(info);

        int maxElementsPerChunk = StreamingChunkBytes / bpe;
        byte[] buf = new byte[maxElementsPerChunk * bpe];
        long elementOffset = 0;
        long bytesRemaining = totalBytes;
        while (bytesRemaining > 0)
        {
            int elementsThisChunk = (int)Math.Min(numElements - elementOffset, maxElementsPerChunk);
            int bytesThisChunk = elementsThisChunk * bpe;
            fs.ReadExactly(buf, 0, bytesThisChunk);
            hasher.Update(buf.AsSpan(0, bytesThisChunk));
            DecodeChunk(
                buf.AsSpan(0, bytesThisChunk),
                info.Dtype,
                result.AsSpan((int)elementOffset, elementsThisChunk));
            elementOffset += elementsThisChunk;
            bytesRemaining -= bytesThisChunk;
        }
    }

    public static List<SafetensorsTensorInfo> ReadHeader(string filePath)
    {
        using FileStream fs = File.OpenRead(filePath);
        Span<byte> sizeBuf = stackalloc byte[8];
        fs.ReadExactly(sizeBuf);
        long headerLen = BinaryPrimitives.ReadInt64LittleEndian(sizeBuf);

        if (headerLen < 2 || headerLen > 100_000_000)
        {
            throw new InvalidDataException($"Implausible safetensors header length {headerLen} in {filePath}");
        }

        byte[] headerBytes = new byte[headerLen];
        fs.ReadExactly(headerBytes);
        long dataStart = 8 + headerLen;

        using JsonDocument doc = JsonDocument.Parse(headerBytes);
        List<SafetensorsTensorInfo> tensors = new(capacity: doc.RootElement.EnumerateObject().Count(p => p.Name != "__metadata__"));

        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "__metadata__")
            {
                continue;
            }

            JsonElement entry = prop.Value;
            string dtypeStr = entry.GetProperty("dtype").GetString()
                ?? throw new InvalidDataException($"Missing dtype for tensor {prop.Name}");
            SafetensorsDtype dtype = ParseDtype(dtypeStr);

            JsonElement shapeElem = entry.GetProperty("shape");
            long[] shape = new long[shapeElem.GetArrayLength()];
            int i = 0;
            foreach (JsonElement dim in shapeElem.EnumerateArray())
            {
                shape[i++] = dim.GetInt64();
            }

            JsonElement offsets = entry.GetProperty("data_offsets");
            long begin = offsets[0].GetInt64();
            long end = offsets[1].GetInt64();

            tensors.Add(new SafetensorsTensorInfo(
                prop.Name,
                dtype,
                shape,
                dataStart + begin,
                dataStart + end,
                filePath));
        }

        return tensors;
    }

    /// <summary>
    /// Stream the tensor's bytes as f64 chunks without ever materializing the
    /// whole tensor in memory. The same chunk buffer is reused across all
    /// invocations of <paramref name="onChunk"/>; callers that need to retain
    /// values must copy. Used by per-tensor statistics passes that must apply
    /// to tensors larger than int.MaxValue elements (e.g. multi-GB FFN weights).
    /// </summary>
    public static void StreamDecode(
        SafetensorsTensorInfo info, Action<ReadOnlySpan<double>> onChunk)
    {
        long numElements = info.ElementCount;
        if (numElements <= 0)
        {
            return;
        }
        int bpe = info.BytesPerElement;
        long totalBytes = info.EndByte - info.BeginByte;
        if (totalBytes != numElements * bpe)
        {
            throw new InvalidDataException(
                $"Tensor {info.Name} size mismatch: {totalBytes} bytes vs expected {numElements * bpe}");
        }

        using Stream fs = OpenTensorStream(info);

        int maxElementsPerChunk = StreamingChunkBytes / bpe;
        byte[] byteBuf = new byte[maxElementsPerChunk * bpe];
        double[] doubleBuf = new double[maxElementsPerChunk];
        long elementOffset = 0;
        long bytesRemaining = totalBytes;
        while (bytesRemaining > 0)
        {
            int elementsThisChunk = (int)Math.Min(numElements - elementOffset, maxElementsPerChunk);
            int bytesThisChunk = elementsThisChunk * bpe;
            fs.ReadExactly(byteBuf, 0, bytesThisChunk);
            DecodeChunk(byteBuf.AsSpan(0, bytesThisChunk), info.Dtype, doubleBuf.AsSpan(0, elementsThisChunk));
            onChunk(doubleBuf.AsSpan(0, elementsThisChunk));
            elementOffset += elementsThisChunk;
            bytesRemaining -= bytesThisChunk;
        }
    }

    public static double[] ReadTensorAsDouble(SafetensorsTensorInfo info)
    {
        long numElements = info.ElementCount;
        if (numElements > int.MaxValue)
        {
            throw new NotSupportedException($"Tensor {info.Name} has {numElements} elements — exceeds int.MaxValue");
        }

        int n = (int)numElements;
        double[] result = new double[n];

        using Stream fs = OpenTensorStream(info);

        int bpe = info.BytesPerElement;
        long totalBytes = info.EndByte - info.BeginByte;
        if (totalBytes != (long)n * bpe)
        {
            throw new InvalidDataException(
                $"Tensor {info.Name} size mismatch: {totalBytes} bytes vs expected {(long)n * bpe} for {n} × {bpe}B");
        }

        byte[] buf = new byte[bpe * Math.Min(n, 8192)];
        int offset = 0;
        while (offset < n)
        {
            int chunk = Math.Min(n - offset, buf.Length / bpe);
            int bytesToRead = chunk * bpe;
            fs.ReadExactly(buf, 0, bytesToRead);
            DecodeChunk(buf.AsSpan(0, bytesToRead), info.Dtype, result.AsSpan(offset, chunk));
            offset += chunk;
        }

        return result;
    }

    private static void DecodeChunk(ReadOnlySpan<byte> src, SafetensorsDtype dtype, Span<double> dst)
    {
        switch (dtype)
        {
            case SafetensorsDtype.F32:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * 4, 4));
                }
                break;
            case SafetensorsDtype.F64:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = BinaryPrimitives.ReadDoubleLittleEndian(src.Slice(i * 8, 8));
                }
                break;
            case SafetensorsDtype.F16:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = (double)BinaryPrimitives.ReadHalfLittleEndian(src.Slice(i * 2, 2));
                }
                break;
            case SafetensorsDtype.BF16:
                for (int i = 0; i < dst.Length; i++)
                {
                    ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
                    uint asFloatBits = (uint)raw << 16;
                    dst[i] = BitConverter.Int32BitsToSingle(unchecked((int)asFloatBits));
                }
                break;
            case SafetensorsDtype.I8:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = (sbyte)src[i];
                }
                break;
            case SafetensorsDtype.U8:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = src[i];
                }
                break;
            case SafetensorsDtype.I16:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(i * 2, 2));
                }
                break;
            case SafetensorsDtype.I32:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(i * 4, 4));
                }
                break;
            case SafetensorsDtype.I64:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = BinaryPrimitives.ReadInt64LittleEndian(src.Slice(i * 8, 8));
                }
                break;
            case SafetensorsDtype.Bool:
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = src[i] != 0 ? 1.0 : 0.0;
                }
                break;
            default:
                throw new NotSupportedException($"Decoding dtype {dtype} as double is not implemented");
        }
    }

    private static SafetensorsDtype ParseDtype(string s) => s switch
    {
        "F32" => SafetensorsDtype.F32,
        "F64" => SafetensorsDtype.F64,
        "F16" => SafetensorsDtype.F16,
        "BF16" => SafetensorsDtype.BF16,
        "I8" => SafetensorsDtype.I8,
        "I16" => SafetensorsDtype.I16,
        "I32" => SafetensorsDtype.I32,
        "I64" => SafetensorsDtype.I64,
        "U8" => SafetensorsDtype.U8,
        "U16" => SafetensorsDtype.U16,
        "U32" => SafetensorsDtype.U32,
        "U64" => SafetensorsDtype.U64,
        "BOOL" => SafetensorsDtype.Bool,
        "F8_E4M3" => SafetensorsDtype.F8E4M3,
        "F8_E5M2" => SafetensorsDtype.F8E5M2,
        _ => throw new NotSupportedException($"Unknown safetensors dtype '{s}'"),
    };
}
