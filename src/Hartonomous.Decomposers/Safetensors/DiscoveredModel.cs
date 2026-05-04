using Hartonomous.Decomposers.Safetensors.Packages;

namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// One ingestible donor model. Origin metadata (publisher, slug, revision) is
/// placement — pins <c>model_source</c>, never the architecture content hash.
///
/// Two ingestion paths coexist:
///   1. Legacy HuggingFace cache shape — <c>SafetensorsFiles</c> populated;
///      <c>Reader</c> null. The orchestrator reads tensor headers via
///      <see cref="SafetensorsReader.ReadHeader(string)"/> and per-tensor bytes
///      via FileStream open + seek.
///   2. Polymorphic donor reader — <c>Reader</c> + <c>ReaderSlot</c> populated
///      (registered with <see cref="DonorReaderRegistry"/>); <c>SafetensorsFiles</c>
///      may be empty. The orchestrator enumerates tensors via
///      <c>Reader.EnumerateTensors()</c>, bridges each to a SafetensorsTensorInfo
///      via <see cref="DonorTensorBridge"/> with a donor:// FilePath, and the
///      static SafetensorsReader streaming helpers route through the reader.
/// </summary>
internal sealed record DiscoveredModel(
    string ModelId,
    string PublisherSlug,
    string ModelSlug,
    byte[] Revision,
    string RevisionHex,
    string ConfigPath,
    IReadOnlyList<string> SafetensorsFiles,
    IDonorPackageReader? Reader = null,
    int ReaderSlot = 0);
