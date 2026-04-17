namespace Hartonomous.Decomposers.Omw;

internal readonly record struct OmwSourceInfo(
    string FilePath,
    string LangCode,
    OmwSourceTier Tier);
