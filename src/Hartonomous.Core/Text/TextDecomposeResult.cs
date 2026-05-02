using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Text;

/// <summary>
/// What <see cref="CanonicalTextDecomposer.Emit"/> returns to the caller.
/// The <see cref="RootHandle"/> + <see cref="RootHash"/> identify the
/// top-level entity in the substrate; counts are diagnostic and gate
/// determinism tests (same UTF-8 input must produce identical counts).
/// <see cref="RootCentroid"/> is the 4D centroid of the top-level
/// composition — useful for callers that want to attach edges referencing
/// this composition's geometric position without re-deriving it.
/// </summary>
public readonly record struct TextDecomposeResult(
    EntityHandle RootHandle,
    byte[] RootHash,
    long EntitiesEmitted,
    long SequenceRowsEmitted,
    long PhysicalityRowsEmitted,
    long SignificanceRowsEmitted,
    (double X, double Y, double Z, double M) RootCentroid);
