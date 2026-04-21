namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-model placement metadata. Lives ONLY on the context — must never be
/// folded into any entity content hash. Same architecture from a different
/// publisher / revision shares the model_architecture entity hash and adds a
/// second entity_model_source junction row pointing here.
/// </summary>
public sealed record ModelSourceHandle(
    long ModelSourceId,
    string PublisherSlug,
    string ModelSlug,
    byte[] Revision,
    string RevisionHex,
    string ModelId,
    string ModelDirectory);
