namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// One ingestible model: a single HuggingFace snapshot directory with a
/// <c>config.json</c> and one or more <c>*.safetensors</c> shards. Identity
/// metadata (publisher, slug, revision) is placement — it pins
/// <c>model_source</c>, never the architecture hash.
/// </summary>
internal sealed record DiscoveredModel(
    string ModelId,
    string PublisherSlug,
    string ModelSlug,
    byte[] Revision,
    string RevisionHex,
    string ConfigPath,
    IReadOnlyList<string> SafetensorsFiles);
