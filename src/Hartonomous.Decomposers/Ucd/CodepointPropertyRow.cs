namespace Hartonomous.Decomposers.Ucd;

internal readonly record struct CodepointPropertyRow(
    long EntityId,
    int CodepointValue,
    int GeneralCategoryId,
    int ScriptId,
    int BlockId,
    int? GcbId,
    int? WbId,
    int? SbId,
    int? LbId,
    bool IsExtendedPictographic,
    short Ccc,
    string? DecompositionType,
    int[]? DecompositionMapping,
    int? SimpleCaseFold,
    int[]? FullCaseFold);
