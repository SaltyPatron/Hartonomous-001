namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.sequence row. Composition ordering — parent contains child
/// at ordinal position N for RleCount consecutive positions. RLE preserves
/// refrains: "the the the" stores child=word_form('the') with ordinal=K and
/// rle_count=3, not three separate rows.
///
/// (parent_hash, ordinal) is unique by construction; the drain function
/// ON CONFLICT DO NOTHING is for re-ingestion idempotence.
/// </summary>
public sealed record SequenceRecord(
    byte[] ParentEntityHash,
    int Ordinal,
    byte[] ChildEntityHash,
    int RleCount = 1) : IngestionRecord;
