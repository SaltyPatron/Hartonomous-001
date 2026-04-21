using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

internal sealed record WiktSound(
    string? Ipa,
    string? Enpr,
    IReadOnlyList<string> Tags,
    string? Audio,
    string? OggUrl,
    string? Mp3Url);
