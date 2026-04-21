using System.Collections.Generic;

namespace Hartonomous.Decomposers.Ud;

/// <summary>
/// One parsed CoNLL-U sentence. SentId is the treebank-local id from "# sent_id = ...".
/// When absent we fall back to the file-relative ordinal, still unique per treebank.
/// </summary>
internal sealed record UdSentenceRecord(
    string SentId,
    string? Text,
    IReadOnlyList<UdTokenRecord> Tokens);
