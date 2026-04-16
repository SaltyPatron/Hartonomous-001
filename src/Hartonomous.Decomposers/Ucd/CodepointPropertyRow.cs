namespace Hartonomous.Decomposers.Ucd;

internal readonly record struct CodepointPropertyRow(
    long EntityId,
    int GeneralCategoryId,
    int ScriptId,
    int BlockId,
    int? GcbId,
    int? WbId,
    int? SbId,
    int? LbId);
