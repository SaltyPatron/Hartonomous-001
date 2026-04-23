using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Tatoeba;

/// <summary>
/// Thin extension of <see cref="BaseReferenceTableWriter"/> for Tatoeba. All seed-time
/// edge types (has_text, translation_link, recording_of, has_contributor) and the
/// <c>tatoeba_sentence</c> entity type are already present in migration 0005, so no
/// Tatoeba-specific ref tables need population — this exists to expose the inherited
/// language-map load + entity_language junction writer under a decomposer-scoped type,
/// matching the convention used by every other decomposer.
/// </summary>
internal sealed class TatoebaReferenceTableWriter : BaseReferenceTableWriter
{
    public TatoebaReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }
}
