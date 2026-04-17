using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Streaming BLAKE3 hasher — wraps the native incremental API. Callers feed
/// bytes in chunks via <see cref="Update"/> and finalize into a caller-allocated
/// 32-byte buffer. Lets large tensors (multi-GB) be hashed without ever
/// allocating a buffer that holds the full content. Not thread-safe.
/// </summary>
public ref struct Blake3Hasher
{
    // Matches hartonomous_blake3_state._opaque size. Heap-allocated once per
    // hasher so the pointer we hand the native layer is stable across multiple
    // Update/Finalize calls (a stack fixed-size buffer inside a ref struct
    // field cannot be addressed without pinning via `fixed`, which must wrap
    // every use — the indirection here is a one-time 2 KiB alloc per model).
    private const int StateSize = 2048;
    private readonly byte[] _state;

    private Blake3Hasher(byte[] state)
    {
        _state = state;
    }

    public static unsafe Blake3Hasher Create()
    {
        byte[] state = new byte[StateSize];
        fixed (byte* p = state)
        {
            NativeCompute.Blake3Init(p);
        }
        return new Blake3Hasher(state);
    }

    public unsafe void Update(scoped ReadOnlySpan<byte> data)
    {
        if (_state is null)
        {
            throw new InvalidOperationException("Blake3Hasher used before Create().");
        }
        if (data.IsEmpty)
        {
            return;
        }
        fixed (byte* s = _state)
        fixed (byte* d = data)
        {
            NativeCompute.Blake3Update(s, d, (nuint)data.Length);
        }
    }

    public unsafe void Finalize(scoped Span<byte> output32)
    {
        if (_state is null)
        {
            throw new InvalidOperationException("Blake3Hasher used before Create().");
        }
        if (output32.Length != Blake3.HashLen)
        {
            throw new ComputeArgumentException($"Blake3Hasher output buffer must be {Blake3.HashLen} bytes");
        }
        fixed (byte* s = _state)
        fixed (byte* o = output32)
        {
            NativeCompute.Blake3Finalize(s, o);
        }
    }

    public byte[] Finalize()
    {
        byte[] output = new byte[Blake3.HashLen];
        Finalize(output);
        return output;
    }
}
