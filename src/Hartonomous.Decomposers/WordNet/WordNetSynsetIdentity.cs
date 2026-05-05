using Hartonomous.Core.Decomposition;

namespace Hartonomous.Decomposers.WordNet;

/// <summary>
/// Single source of truth for the WordNet synset offset-code → hash convention.
/// Both WordNet's own <c>has_wordnet_offset</c> emission and OMW's cross-lexicon
/// offset-to-synset_hash resolution must produce the SAME hash bytes from the
/// SAME offset string — any drift between the two would silently break OMW's
/// alignment without raising an error. This helper is the named contract;
/// callers in either decomposer go through it instead of independently
/// formatting + hashing.
/// </summary>
public static class WordNetSynsetIdentity
{
    /// <summary>
    /// Hash a WordNet offset code (e.g. "00001740-n") to the bytes used as
    /// the entity hash for the offset's <c>text_composition</c> entity. WordNet
    /// emits this entity; OMW looks it up via the same hash to find the
    /// linked synset.
    /// </summary>
    public static byte[] OffsetCodeHash(string offsetCode) =>
        BaseDecomposer.ComputeAtomicStringHash(offsetCode);
}
