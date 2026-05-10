using System.Collections.Generic;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Dispatch table from tensor role code to the registered
/// <see cref="ILayerTypeSynthesizer"/>. The recomposer queries this for every
/// target tensor and routes to the matching synthesizer. Composition root
/// constructs the registry from the DI-registered set of synthesizers; one
/// synthesizer can claim multiple role codes (e.g. <c>FfnLayerSynthesizer</c>
/// handles "ffn_gate" / "ffn_up" / "ffn_down" together).
///
/// Per Phase C.0 of the implementation plan + spec §VI dispatch contract.
/// </summary>
public interface ILayerTypeSynthesizerRegistry
{
    /// <summary>
    /// Get the synthesizer registered for this role code. Returns null when
    /// no synthesizer is registered (recomposer treats this as honest-
    /// abstention zero for the tensor and reports the missing role in the
    /// recomposition report).
    /// </summary>
    ILayerTypeSynthesizer? GetSynthesizer(string roleCode);

    /// <summary>All registered synthesizers — for diagnostics and coverage reports.</summary>
    IReadOnlyCollection<ILayerTypeSynthesizer> AllSynthesizers { get; }
}
