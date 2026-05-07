using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Data;

/// <summary>
/// Reads physicality content from the substrate. Used by recomposers that
/// need geometric content (point4d coordinates, linestring4d vertex
/// sequences) attached to entities — for example, the SafetensorsRecomposer
/// walking has_rank_component edges and pulling each component's
/// linestring4d (U⊕V packed) to reconstruct tensor weights via U·diag(σ)·Vᵀ.
///
/// Hash-as-PK: every method addresses the entity by composite handle.
/// </summary>
public interface IPhysicalityReader
{
    /// <summary>
    /// Read the linestring4d (4D-trajectory) physicality of the given type
    /// attached to <paramref name="entity"/>. Returns the flat coordinate
    /// array (length 4·n where n is the vertex count) in the same order as
    /// the original AddPhysicalityLineString4d emission. Returns null if no
    /// physicality of that type is attached.
    /// </summary>
    Task<double[]?> GetLineString4dAsync(
        EntityHandle entity, string physicalityTypeCode, CancellationToken ct);

    /// <summary>
    /// Read the point4d physicality of the given type attached to
    /// <paramref name="entity"/>. Returns the 4 coordinates (x1, x2, x3, x4)
    /// or null if no physicality of that type is attached.
    /// </summary>
    Task<(double X1, double X2, double X3, double X4)?> GetPoint4dAsync(
        EntityHandle entity, string physicalityTypeCode, CancellationToken ct);
}
