namespace Hartonomous.Recomposers;

/// <summary>
/// The text-side artifacts of a recomposed model package: every textual
/// configuration / tokenizer / template file the original model shipped with,
/// reconstituted from the substrate's content-addressed text DAGs.
///
/// Tensor weight reconstruction is a separate concern — the substrate stores
/// fireflies + archetype edges, not raw weight bytes, so weight recomposition
/// is lottery-ticket synthesis (not byte-perfect roundtrip) and is handled by
/// a distinct flow.
///
/// Each property is null when the source model didn't ship that artifact (or
/// when no <c>has_*_artifact</c> edge was emitted for it during ingestion).
/// </summary>
public sealed record ModelArtifactsPackage(
    string? ConfigJson,
    string? TokenizerJson,
    string? TokenizerConfigJson,
    string? SpecialTokensMapJson,
    string? MergesTxt,
    string? ChatTemplateJinja,
    string? GenerationConfigJson,
    string? Readme);
