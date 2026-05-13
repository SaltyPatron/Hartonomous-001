using System.Collections.Generic;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Substrate;

/// <summary>
/// One tier in a composition walk. <see cref="Depth"/> is 0 at the root,
/// 1 at the root's direct children, and so on. <see cref="Entities"/> is the
/// flat in-order list of child entities at this depth across all parents in
/// the previous tier (vertex-order within each parent, parent-order across
/// the tier).
/// </summary>
public readonly record struct TierFrame(int Depth, IReadOnlyList<EntityHandle> Entities);
