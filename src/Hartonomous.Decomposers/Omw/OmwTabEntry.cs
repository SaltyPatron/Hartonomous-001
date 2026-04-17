namespace Hartonomous.Decomposers.Omw;

internal readonly record struct OmwTabEntry(
    string SynsetCode,
    string LangCode,
    string Relation,
    string Word);
