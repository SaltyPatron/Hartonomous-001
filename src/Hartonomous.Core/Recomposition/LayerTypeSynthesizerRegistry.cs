using System.Collections.Generic;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Default in-memory dispatch registry built from a set of registered
/// synthesizers. Constructed in the composition root (Hartonomous.Cli /
/// Hartonomous.Api) from the DI-resolved <see cref="ILayerTypeSynthesizer"/>
/// collection. Last-registration-wins on conflicting role codes (allows
/// recipe-scoped synthesizer overrides for advanced use).
/// </summary>
public sealed class LayerTypeSynthesizerRegistry : ILayerTypeSynthesizerRegistry
{
    private readonly Dictionary<string, ILayerTypeSynthesizer> _byRole;
    private readonly List<ILayerTypeSynthesizer> _all;

    public LayerTypeSynthesizerRegistry(IEnumerable<ILayerTypeSynthesizer> synthesizers)
    {
        _byRole = new Dictionary<string, ILayerTypeSynthesizer>();
        _all = new List<ILayerTypeSynthesizer>();
        foreach (ILayerTypeSynthesizer s in synthesizers)
        {
            _all.Add(s);
            foreach (string code in s.TargetRoleCodes)
            {
                _byRole[code] = s;
            }
        }
    }

    public ILayerTypeSynthesizer? GetSynthesizer(string roleCode)
    {
        return _byRole.TryGetValue(roleCode, out ILayerTypeSynthesizer? s) ? s : null;
    }

    public IReadOnlyCollection<ILayerTypeSynthesizer> AllSynthesizers => _all;
}
