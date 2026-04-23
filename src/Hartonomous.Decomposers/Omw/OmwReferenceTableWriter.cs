using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Omw;

internal sealed class OmwReferenceTableWriter : BaseReferenceTableWriter
{
    public OmwReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }

    /// <summary>
    /// Load the synset code → gloss mapping from <c>substrate.sense</c> so OMW can compute
    /// the same content-based synset hash that WordNet produced.
    /// </summary>
    public Task<Dictionary<string, string>> LoadSynsetGlossMapAsync(CancellationToken ct) =>
        LoadCodeTextMapAsync("substrate.sense", "gloss", 120_000, ct);
}
