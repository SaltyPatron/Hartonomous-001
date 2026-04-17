using System;
using System.Buffers.Binary;
using System.Text;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Concrete <see cref="ICanonicalSignatureBuilder"/>. Streams every field into
/// an incremental BLAKE3 hasher provided by the compute facade — never
/// materializes the full signature blob in memory, so multi-MiB packed-vector
/// signatures (e.g. SVD spectra over thousands of singular values) are safe.
///
/// Encoding convention:
///     [kind:4 ASCII bytes]
///     [field bytes ...]
///
/// Field encoders:
///     WriteInt32LE       → 4 little-endian bytes
///     WriteInt64LE       → 8 little-endian bytes
///     WriteDouble        → IEEE 754 binary64, big-endian (stable across
///                          architectures regardless of host endianness)
///     WriteUtf8          → 4-byte LE length + UTF-8 bytes
///     WriteBytes         → 8-byte LE length + raw bytes
///     WriteHash          → 4-byte LE length + 32 bytes (length always 32, but
///                          encoded so a future hash-length change is detectable)
///
/// Not thread-safe. One builder per signature; do not reuse after Finalize.
/// </summary>
public sealed class CanonicalSignatureBuilder : ICanonicalSignatureBuilder
{
    private Blake3Hasher _hasher;
    private bool _finalized;

    public CanonicalSignatureBuilder(ICommonCompute compute, ReadOnlySpan<byte> kindTag4)
    {
        if (kindTag4.Length != 4)
        {
            throw new ArgumentException($"Kind tag must be exactly 4 bytes, got {kindTag4.Length}", nameof(kindTag4));
        }
        _hasher = compute.CreateBlake3Hasher();
        _hasher.Update(kindTag4);
    }

    public CanonicalSignatureBuilder(ICommonCompute compute, string kindTag4)
        : this(compute, Encoding.ASCII.GetBytes(kindTag4))
    {
    }

    public ICanonicalSignatureBuilder WriteInt32LE(int value)
    {
        EnsureLive();
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        _hasher.Update(buf);
        return this;
    }

    public ICanonicalSignatureBuilder WriteInt64LE(long value)
    {
        EnsureLive();
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        _hasher.Update(buf);
        return this;
    }

    public ICanonicalSignatureBuilder WriteDouble(double value)
    {
        EnsureLive();
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buf, value);
        _hasher.Update(buf);
        return this;
    }

    public ICanonicalSignatureBuilder WriteUtf8(ReadOnlySpan<char> value)
    {
        EnsureLive();
        int byteLen = Encoding.UTF8.GetByteCount(value);
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, byteLen);
        _hasher.Update(lenBuf);

        // 1 KiB scratch handles the vast majority of identifiers; large blobs
        // fall back to a heap allocation (still streamed into the hasher, never
        // materialized as a single signature buffer).
        if (byteLen <= 1024)
        {
            Span<byte> buf = stackalloc byte[byteLen];
            Encoding.UTF8.GetBytes(value, buf);
            _hasher.Update(buf);
        }
        else
        {
            byte[] heap = new byte[byteLen];
            Encoding.UTF8.GetBytes(value, heap);
            _hasher.Update(heap);
        }
        return this;
    }

    public ICanonicalSignatureBuilder WriteBytes(ReadOnlySpan<byte> value)
    {
        EnsureLive();
        Span<byte> lenBuf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lenBuf, value.Length);
        _hasher.Update(lenBuf);
        _hasher.Update(value);
        return this;
    }

    public ICanonicalSignatureBuilder WriteHash(ReadOnlySpan<byte> blake32)
    {
        EnsureLive();
        if (blake32.Length != Blake3.HashLen)
        {
            throw new ArgumentException(
                $"WriteHash expects a {Blake3.HashLen}-byte BLAKE3 hash, got {blake32.Length}",
                nameof(blake32));
        }
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, blake32.Length);
        _hasher.Update(lenBuf);
        _hasher.Update(blake32);
        return this;
    }

    public byte[] Finalize()
    {
        EnsureLive();
        _finalized = true;
        return _hasher.Finalize();
    }

    private void EnsureLive()
    {
        if (_finalized)
        {
            throw new InvalidOperationException("CanonicalSignatureBuilder cannot be reused after Finalize().");
        }
    }
}
