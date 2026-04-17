namespace Hartonomous.Decomposers.Iso639;

internal readonly record struct Iso639Record(
    string Id,
    string? Part2b,
    string? Part2t,
    string? Part1,
    char Scope,
    char LanguageType,
    string RefName);
