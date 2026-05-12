using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Shared infrastructure for <see cref="ILayerTypeSynthesizer"/> implementations.
/// Provides:
/// <list type="bullet">
/// <item>Dtype-pack helpers (f64 → wire dtype with honest-abstention zeros
/// preserved exactly through the round-trip).</item>
/// <item>Coverage-aggregate utility for producing <see cref="SynthesisResult"/>.</item>
/// <item>Source-filter checking for Mode 1 vs Mode 2 attestation walks.</item>
/// </list>
///
/// Per Phase C.0. Concrete synthesizers (Phase C.1) extend this and supply
/// the per-role math via <see cref="SynthesizeF64Async"/>.
/// </summary>
public abstract class LayerTypeSynthesizerBase : ILayerTypeSynthesizer
{
    /// <inheritdoc/>
    public abstract IReadOnlyList<string> TargetRoleCodes { get; }

    /// <inheritdoc/>
    public abstract string AttestationTypeCode { get; }

    /// <inheritdoc/>
    public abstract string SourceEdgeTypeCode { get; }

    /// <summary>
    /// Per-role exact synthesis. Subclasses produce the row-major f64 weight
    /// matrix and the per-cell coverage matrix; the base class handles the
    /// honest-abstention masking + dtype pack to the wire format.
    /// </summary>
    /// <param name="targetTensor">Target tensor spec.</param>
    /// <param name="context">Synthesis context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>(weights f64 row-major, coverage f64 row-major in [0,1] per
    /// cell, attestation count, contributing source count).</returns>
    protected abstract Task<(double[] Weights, double[] Coverage, long AttestationCount, int ContributingSourceCount)>
        SynthesizeF64Async(
            TargetTensorSpec targetTensor,
            SynthesisContext context,
            CancellationToken ct);

    /// <inheritdoc/>
    public async Task<SynthesisResult> SynthesizeAsync(
        TargetTensorSpec targetTensor,
        SynthesisContext context,
        CancellationToken ct)
    {
        ValidateRole(targetTensor.RoleCode);

        long elementCount = ComputeElementCount(targetTensor.Shape);
        if (elementCount <= 0)
        {
            return new SynthesisResult(
                Bytes: Array.Empty<byte>(),
                AggregateCoverage: 0.0,
                AttestationCount: 0,
                ContributingSourceCount: 0);
        }

        (double[] weights, double[] coverage, long attestationCount, int contributingSourceCount) =
            await SynthesizeF64Async(targetTensor, context, ct);

        if (weights.Length != elementCount)
        {
            throw new SynthesisShapeMismatchException(
                $"{GetType().Name}: SynthesizeF64Async returned {weights.Length} elements, target requires {elementCount}");
        }
        if (coverage.Length != elementCount)
        {
            throw new SynthesisShapeMismatchException(
                $"{GetType().Name}: coverage matrix length {coverage.Length} does not match weight length {elementCount}");
        }

        long rows = targetTensor.Shape.Count > 0 ? targetTensor.Shape[0] : 1L;
        long cols = elementCount / Math.Max(1L, rows);
        double[] rowCoverage = new double[Math.Max(1L, rows)];

        double aggregateCoverage = Hartonomous.Core.Compute.Common.HonestAbstentionFiller.ApplyF64(
            rows, cols,
            weights.AsSpan(),
            coverage.AsSpan(),
            cellThreshold: ResolveAbstentionThreshold(context),
            rowCoverageOut: rowCoverage.AsSpan());

        byte[] packed = PackToWire(weights, targetTensor.Dtype);

        return new SynthesisResult(
            Bytes: packed,
            AggregateCoverage: aggregateCoverage,
            AttestationCount: attestationCount,
            ContributingSourceCount: contributingSourceCount);
    }

    private void ValidateRole(string roleCode)
    {
        for (int i = 0; i < TargetRoleCodes.Count; i++)
        {
            if (TargetRoleCodes[i] == roleCode) { return; }
        }
        throw new SynthesisDispatchException(
            $"{GetType().Name}: target tensor role '{roleCode}' not in TargetRoleCodes ({string.Join(", ", TargetRoleCodes)})");
    }

    private static long ComputeElementCount(IReadOnlyList<long> shape)
    {
        if (shape.Count == 0) { return 0; }
        long n = 1;
        for (int i = 0; i < shape.Count; i++)
        {
            n = checked(n * shape[i]);
            if (n <= 0) { return 0; }
        }
        return n;
    }

    /// <summary>
    /// Per-tensor-type abstention threshold. Defaults to 0.10 — overridable
    /// via subclass for per-role variation (LayerNorm wants 0.0 for always-cover;
    /// embedding wants 0.05 for fewer-attestations-OK behavior).
    /// </summary>
    protected virtual double ResolveAbstentionThreshold(SynthesisContext context)
    {
        return 0.10;
    }

    private static byte[] PackToWire(double[] f64Values, string dtype)
    {
        switch (dtype)
        {
            case "F64":
                return PackF64(f64Values);
            case "F32":
                return PackF32(f64Values);
            case "BF16":
                return PackBf16(f64Values);
            case "F16":
                return PackF16(f64Values);
            default:
                throw new SynthesisDtypeException(
                    $"PackToWire: unsupported wire dtype '{dtype}' (supported: F64, F32, BF16, F16)");
        }
    }

    private static byte[] PackF64(double[] values)
    {
        byte[] buf = new byte[values.Length * 8];
        Buffer.BlockCopy(values, 0, buf, 0, buf.Length);
        return buf;
    }

    private static byte[] PackF32(double[] values)
    {
        byte[] buf = new byte[values.Length * 4];
        Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf.AsSpan());
        for (int i = 0; i < values.Length; i++)
        {
            floats[i] = (float)values[i];
        }
        return buf;
    }

    private static byte[] PackBf16(double[] values)
    {
        byte[] buf = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            float f = (float)values[i];
            uint bits = System.Runtime.CompilerServices.Unsafe.As<float, uint>(ref f);
            // Round-to-nearest-even; deterministic, matches PyTorch's BF16 cast.
            uint rounding = (bits >> 16) & 1u;
            uint rounded = (bits + 0x7FFFu + rounding) >> 16;
            ushort bf16 = (ushort)rounded;
            buf[i * 2] = (byte)(bf16 & 0xFF);
            buf[i * 2 + 1] = (byte)((bf16 >> 8) & 0xFF);
        }
        return buf;
    }

    private static byte[] PackF16(double[] values)
    {
        byte[] buf = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            Half h = (Half)(float)values[i];
            short bits = System.Runtime.CompilerServices.Unsafe.As<Half, short>(ref h);
            buf[i * 2] = (byte)(bits & 0xFF);
            buf[i * 2 + 1] = (byte)((bits >> 8) & 0xFF);
        }
        return buf;
    }
}
