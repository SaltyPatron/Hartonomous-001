using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.WordNet;

/// <summary>
/// WordNet-specific reference-table writer. Inherits POS/language maps, entity_pos /
/// entity_language junction writers, and edge_type upserts from
/// <see cref="BaseReferenceTableWriter"/>; adds lexname/sense loaders, sense population,
/// English-language-id lookup, and the per-entry-mu entity_sense junction writer.
/// </summary>
internal sealed class WordNetReferenceTableWriter : BaseReferenceTableWriter
{
    public WordNetReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }

    public Task<Dictionary<string, int>> LoadLexnameMapAsync(CancellationToken ct) =>
        LoadCodeMapAsync("substrate.lexname", 50, ct);

    public Task<Dictionary<string, int>> LoadSenseMapAsync(CancellationToken ct) =>
        LoadCodeMapAsync("substrate.sense", 120_000, ct);

    public Task<int> LoadEnglishLanguageIdAsync(CancellationToken ct) =>
        LoadIdByCodeAsync("substrate.language", "eng", ct);

    public Task PopulateSensesAsync(
        IReadOnlyList<(string Code, string Gloss, int LexnameId, int PosId)> senses,
        CancellationToken ct) =>
        PopulateSenseRowsAsync(senses, ct);

    public Task WriteEntitySenseJunctionsAsync(
        IReadOnlyList<(long EntityId, int SenseId, double Mu)> entries, CancellationToken ct) =>
        WriteGlickoJunctionAsync("substrate.entity_sense", "sense_id", entries, ct);
}
