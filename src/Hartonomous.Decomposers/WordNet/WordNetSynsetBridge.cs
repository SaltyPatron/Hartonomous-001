using System.Collections.Concurrent;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Decomposers.WordNet;

public sealed class WordNetSynsetBridge : IWordNetSynsetBridge
{
    private readonly ConcurrentDictionary<string, Hash32> _synsetHashes =
        new(Environment.ProcessorCount, 120_000, StringComparer.Ordinal);

    public int Count => _synsetHashes.Count;

    public void Add(string offsetCode, Hash32 synsetHash)
    {
        _synsetHashes[offsetCode] = synsetHash;
    }

    public bool TryGetSynsetHash(string offsetCode, out Hash32 synsetHash)
    {
        return _synsetHashes.TryGetValue(offsetCode, out synsetHash);
    }
}
