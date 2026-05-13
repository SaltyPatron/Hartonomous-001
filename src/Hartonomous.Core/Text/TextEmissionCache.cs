using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Text;

public sealed class TextEmissionCache : ITextEmissionCache
{
    private const int CapacityPerKind = 4_194_304;

    private readonly ConcurrentDictionary<Hash32, byte> _entities = new();
    private readonly ConcurrentDictionary<Hash32, byte> _physicalities = new();
    private readonly ConcurrentDictionary<Hash32, byte> _compositionChildren = new();
    private readonly ConcurrentDictionary<Hash32, byte> _significances = new();
    private long _entityCount;
    private long _physicalityCount;
    private long _compositionChildCount;
    private long _significanceCount;

    public bool TryRegisterEntity(string entityTypeCode, Hash32 hash, string provenanceCode)
        => TryRegister(_entities, ref _entityCount, ComposeKey(entityTypeCode, provenanceCode, hash));

    public bool TryRegisterPhysicality(string physicalityTypeCode, Hash32 entityHash)
        => TryRegister(_physicalities, ref _physicalityCount, ComposeKey(physicalityTypeCode, entityHash));

    public bool TryRegisterCompositionChild(Hash32 parentHash, int ordinal)
        => TryRegister(_compositionChildren, ref _compositionChildCount, ComposeKey(parentHash, ordinal));

    public bool TryRegisterSignificance(string contextTypeCode, string attestationTypeCode, Hash32 entityHash)
        => TryRegister(_significances, ref _significanceCount, ComposeKey(contextTypeCode, attestationTypeCode, entityHash));

    private static bool TryRegister(
        ConcurrentDictionary<Hash32, byte> cache,
        ref long approximateCount,
        Hash32 key)
    {
        if (Volatile.Read(ref approximateCount) >= CapacityPerKind)
        {
            cache.Clear();
            Volatile.Write(ref approximateCount, 0);
        }

        if (!cache.TryAdd(key, 0))
        {
            return false;
        }

        Interlocked.Increment(ref approximateCount);
        return true;
    }

    private static Hash32 ComposeKey(string code, Hash32 hash)
    {
        byte[] codeBytes = Encoding.UTF8.GetBytes(code);
        byte[] buf = new byte[codeBytes.Length + 1 + Hash32.Length];
        Buffer.BlockCopy(codeBytes, 0, buf, 0, codeBytes.Length);
        buf[codeBytes.Length] = 0x1F;
        hash.CopyTo(buf.AsSpan(codeBytes.Length + 1, Hash32.Length));
        return Blake3.Hash32(buf);
    }

    private static Hash32 ComposeKey(string codeA, string codeB, Hash32 hash)
    {
        byte[] aBytes = Encoding.UTF8.GetBytes(codeA);
        byte[] bBytes = Encoding.UTF8.GetBytes(codeB);
        byte[] buf = new byte[aBytes.Length + 1 + bBytes.Length + 1 + Hash32.Length];
        int offset = 0;
        Buffer.BlockCopy(aBytes, 0, buf, offset, aBytes.Length);
        offset += aBytes.Length;
        buf[offset++] = 0x1F;
        Buffer.BlockCopy(bBytes, 0, buf, offset, bBytes.Length);
        offset += bBytes.Length;
        buf[offset++] = 0x1F;
        hash.CopyTo(buf.AsSpan(offset, Hash32.Length));
        return Blake3.Hash32(buf);
    }

    private static Hash32 ComposeKey(Hash32 hash, int ordinal)
    {
        Span<byte> buf = stackalloc byte[Hash32.Length + 4];
        hash.CopyTo(buf);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            buf.Slice(Hash32.Length, 4),
            ordinal);
        return Blake3.Hash32(buf);
    }
}
