using System.Diagnostics.CodeAnalysis;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Decomposers.WordNet;

public interface IWordNetSynsetBridge
{
    int Count { get; }

    void Add(string offsetCode, Hash32 synsetHash);

    bool TryGetSynsetHash(string offsetCode, out Hash32 synsetHash);
}
