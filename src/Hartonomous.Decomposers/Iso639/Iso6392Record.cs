namespace Hartonomous.Decomposers.Iso639;

/// <summary>
/// One ISO 639-2 record (Library of Congress alpha-3 + alpha-2 + English/French names).
/// </summary>
/// <param name="Alpha3Bibliographic">Three-letter bibliographic code (the historical / French-derived one for languages with bibliographic vs terminologic split; otherwise same as terminologic).</param>
/// <param name="Alpha3Terminologic">Three-letter terminologic code (the linguistic preference; null when same as bibliographic).</param>
/// <param name="Alpha2">Two-letter ISO 639-1 code; null when none exists.</param>
/// <param name="EnglishName">English name (may contain semicolons for multi-name entries).</param>
/// <param name="FrenchName">French name (may contain semicolons for multi-name entries).</param>
internal sealed record Iso6392Record(
    string Alpha3Bibliographic,
    string? Alpha3Terminologic,
    string? Alpha2,
    string EnglishName,
    string FrenchName);
