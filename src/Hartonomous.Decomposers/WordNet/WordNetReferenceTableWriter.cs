using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.WordNet;

/// <summary>
/// WordNet-specific reference-table writer. Inherits POS/language maps and
/// junction writers from <see cref="BaseReferenceTableWriter"/>; adds lexname
/// loader and English-language-id lookup. The earlier sense-reference-table
/// methods (LoadSenseMapAsync / PopulateSensesAsync / WriteEntitySenseJunctionsAsync)
/// were removed when substrate.sense + substrate.entity_sense were eliminated:
/// WordNet senses are first-class has_sense edges (lemma → synset) with
/// per-arena Glicko ratings on substrate.edge_significance.
/// </summary>
internal sealed class WordNetReferenceTableWriter : BaseReferenceTableWriter
{
    public WordNetReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }

    public Task<Dictionary<string, int>> LoadLexnameMapAsync(CancellationToken ct) =>
        LoadCodeMapAsync("substrate.lexname", 50, ct);

    public Task<int> LoadEnglishLanguageIdAsync(CancellationToken ct) =>
        LoadIdByCodeAsync("substrate.language", "eng", ct);
}
