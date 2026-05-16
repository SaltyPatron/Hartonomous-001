using System.Collections.Generic;

namespace Hartonomous.Decomposers.Iso639;

/// <summary>
/// One IANA BCP47 language-subtag-registry record. The registry has Type ∈
/// {language, extlang, script, region, variant, grandfathered, redundant};
/// this DTO covers the load-bearing language + script + region types.
/// </summary>
/// <param name="Type">"language" / "extlang" / "script" / "region" / "variant" / "grandfathered" / "redundant".</param>
/// <param name="Subtag">The canonical subtag (e.g. "en", "Latn", "US", "1996").</param>
/// <param name="Descriptions">Description lines (a registry record may have multiple Description: lines).</param>
/// <param name="Added">ISO-8601 date the subtag was added to the registry.</param>
/// <param name="SuppressScript">"Suppress-Script" subtag if present (e.g. "Latn" for "en" — script is implicit).</param>
/// <param name="Scope">"Scope" value if present ("macrolanguage" / "collection" / "private-use" / "special").</param>
/// <param name="Macrolanguage">"Macrolanguage" code if present (the parent macrolanguage).</param>
/// <param name="DeprecatedDate">"Deprecated" date if present (subtag is deprecated as of this date).</param>
/// <param name="PreferredValue">"Preferred-Value" if present (the canonical replacement when deprecated).</param>
/// <param name="Prefix">Prefix subtag list if Type = extlang/variant.</param>
internal sealed record Bcp47Record(
    string Type,
    string Subtag,
    List<string> Descriptions,
    string? Added,
    string? SuppressScript,
    string? Scope,
    string? Macrolanguage,
    string? DeprecatedDate,
    string? PreferredValue,
    List<string> Prefix);
