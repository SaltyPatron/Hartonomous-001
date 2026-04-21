namespace Hartonomous.Decomposers.Ud;

/// <summary>
/// One (key, value) pair inside a CoNLL-U FEATS column. Values may be comma-separated
/// (e.g. "Case=Acc,Nom") — that whole comma-list is stored as a single value per UD spec.
/// </summary>
internal readonly record struct UdMorphFeature(string Key, string Value);
