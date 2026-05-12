using System.Collections.Generic;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Marker interface for passes that also satisfy the metadata-decomposer
/// contract while using the model-pass lifecycle.
/// </summary>
internal interface IMetadataDecomposerPass : IModelAnalysisPass
{
    IReadOnlyList<string> AcceptedFilePatterns { get; }
}
