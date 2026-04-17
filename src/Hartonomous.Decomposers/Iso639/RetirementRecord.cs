namespace Hartonomous.Decomposers.Iso639;

internal readonly record struct RetirementRecord(
    string Id,
    string RefName,
    char RetReason,
    string? ChangeTo,
    string? RetRemedy,
    string EffectiveDate);
