using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// Wiktionary uses only the shared surface: POS/language/morph-feature maps,
/// structural + cross_lingual edge-type upserts, and the entity_pos / entity_language /
/// entity_morph_feature junction writers — all inherited from
/// <see cref="BaseReferenceTableWriter"/>. No Wiktionary-specific ref tables exist.
/// </summary>
internal sealed class WiktionaryReferenceTableWriter : BaseReferenceTableWriter
{
    public WiktionaryReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }
}
