using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Iso639;

/// <summary>
/// ISO 639 ref-table writer. Inherits the language code→id loader + entity_language
/// junction writer from <see cref="BaseReferenceTableWriter"/>; adds the
/// ISO-639 language row populator and the <c>language.name_entity_id</c> back-fill.
/// </summary>
internal sealed class Iso639ReferenceTableWriter : BaseReferenceTableWriter
{
    public Iso639ReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }

    public Task PopulateLanguagesAsync(
        IReadOnlyList<Iso639Record> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return Task.CompletedTask;
        }

        List<(string Code, string Name, string Scope, string Type, string? Part1, string? Part2B, string? Part2T)> rows = new(records.Count);
        foreach (Iso639Record record in records)
        {
            rows.Add((
                record.Id,
                record.RefName,
                record.Scope.ToString(),
                record.LanguageType.ToString(),
                record.Part1,
                record.Part2b,
                record.Part2t));
        }

        return PopulateLanguagesCoreAsync(rows, ct);
    }

    public async Task WriteLanguageJunctionsAsync(
        IReadOnlyList<(string Code, byte[] NameHash)> nameEntities,
        Dictionary<string, int> langIdMap,
        CancellationToken ct)
    {
        // language_name entities live in entity partition 18 (entity_unicode).
        // Build the (entity_type_id, entity_hash, language_id) triples directly
        // — no resolve, no surrogate id.
        const int LanguageNameEntityTypeId = 18;
        List<(int EntityTypeId, byte[] EntityHash, int LangId)> entries = new(nameEntities.Count);
        foreach ((string code, byte[] nameHash) in nameEntities)
        {
            if (langIdMap.TryGetValue(code, out int langId))
            {
                entries.Add((LanguageNameEntityTypeId, nameHash, langId));
            }
        }

        await WriteEntityLanguageJunctionsAsync(entries, ct);
    }

    public Task UpdateNameEntityIdsAsync(
        IReadOnlyList<(string Code, byte[] NameHash)> updates, CancellationToken ct) =>
        UpdateLanguageNameEntityIdsCoreAsync(updates, ct);
}
