using System.Collections.Generic;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Per-recomposition aggregate statistics written to safetensors header
/// metadata for audit and coverage reporting.
/// </summary>
public sealed record RecompositionReport(
    int TensorCount,
    long TotalBytes,
    double MeanCoverage,
    double MinCoverage,
    double ZeroFractionMean,
    int ContributingSourceCount,
    IReadOnlyDictionary<string, double> PerTensorCoverage);
