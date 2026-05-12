using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Query;

/// <summary>
/// One ordered tensor entry from a concrete source package.
/// </summary>
public sealed record PackageTensorHandle(
    EntityHandle Package,
    int Ordinal,
    EntityHandle Occurrence,
    EntityHandle Tensor);
