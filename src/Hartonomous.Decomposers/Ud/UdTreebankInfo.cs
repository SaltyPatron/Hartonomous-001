using System.Collections.Generic;

namespace Hartonomous.Decomposers.Ud;

/// <summary>
/// One UD_{Language}-{Treebank} directory. <see cref="LanguageCode"/> is the ISO 639-3
/// inferred from the file prefix (e.g. "en_ewt-ud-train.conllu" → "eng" after mapping);
/// treebanks without a resolvable language code are skipped for ingestion.
/// </summary>
internal sealed record UdTreebankInfo(
    string DirectoryName,
    string TreebankName,
    string LanguageName,
    string? LanguageCode,
    IReadOnlyList<string> ConlluFiles);
