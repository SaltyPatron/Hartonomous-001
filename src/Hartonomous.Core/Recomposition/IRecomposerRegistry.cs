using System.Collections.Generic;

using Hartonomous.Core.Analysis;

namespace Hartonomous.Core.Recomposition;

public interface IRecomposerRegistry
{
    IReadOnlyCollection<Modality> RegisteredModalities { get; }

    IRecomposer<TTarget> Resolve<TTarget>(Modality modality) where TTarget : notnull;

    bool TryResolve<TTarget>(Modality modality, out IRecomposer<TTarget>? recomposer) where TTarget : notnull;
}
