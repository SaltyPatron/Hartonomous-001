using System.Collections.Generic;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Data;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Per-recomposition-session context passed to every
/// <see cref="ILayerTypeSynthesizer"/>. Carries the source filter (Mode 1
/// vs Mode 2 mu blending), arena weighting, abstention threshold, target
/// architecture spec (Mode 2), and the substrate query interfaces the
/// synthesizer reads attestations through.
/// </summary>
/// <param name="Options">Caller-supplied recomposition options (arena codes,
/// significance threshold, attestation-type filter / blend, recipe id, etc.).</param>
/// <param name="SourceModelIds">When non-null, restrict consensus contribution
/// to these model_source_ids only. Mode 1 re-export passes [model_source_id];
/// Mode 2 substrate synthesis passes null (default = all ingested models contribute).</param>
/// <param name="TargetArchitecture">Mode 2 only: the target architecture spec
/// the recomposer is building toward. Null in Mode 1 (the substrate's stored
/// tree IS the target).</param>
/// <param name="EntityReader">Substrate entity / edge query surface.</param>
/// <param name="PhysicalityReader">Substrate physicality query surface
/// (firefly POINTZMs, edge LINESTRINGZM trajectories, tensor contour
/// vectors).</param>
/// <param name="Compute">Compute facade — synthesizers call into the
/// synthesis primitives via Compute.Common.LinearSystemSolver /
/// SparseFfnInversion / InverseLaplacianEigenmap / HonestAbstentionFiller.</param>
public sealed record SynthesisContext(
    RecompositionOptions Options,
    IReadOnlyList<long>? SourceModelIds,
    TargetArchitectureSpec? TargetArchitecture,
    IEntityReader EntityReader,
    IPhysicalityReader PhysicalityReader,
    IComputeFacade Compute);
