using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Ud;

/// <summary>
/// UD-specific reference-table writer. Inherits the shared POS/morph-feature/language
/// loaders, junction writers, and edge_type upsert from <see cref="BaseReferenceTableWriter"/>;
/// adds the deprel loader, deprel populator (with parent-id resolution for subtyped
/// relations like "acl:relcl"), and the deprel→edge_type bulk upsert.
/// </summary>
internal sealed class UdReferenceTableWriter : BaseReferenceTableWriter
{
    public UdReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }

    public Task<Dictionary<string, int>> LoadDeprelMapAsync(CancellationToken ct) =>
        LoadCodeMapAsync("substrate.deprel", 128, ct);

    public Task PopulateDeprelsAsync(
        IReadOnlyCollection<string> deprels, CancellationToken ct) =>
        PopulateDeprelsCoreAsync(deprels, ct);

    public Task UpsertDeprelEdgeTypesAsync(
        IReadOnlyCollection<string> deprels,
        CancellationToken ct) =>
        UpsertHomogeneousEdgeTypesAsync(deprels, "structural", "word_form", ct);
}
