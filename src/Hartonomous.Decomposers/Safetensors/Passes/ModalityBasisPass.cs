using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for VISION_PROJECTION and MODALITY_PROJECTION
/// tensors. Each row is one basis vector in the target modality's
/// channel space — the direction the projection writes one channel of
/// modality output as a function of the source residual stream. Hashed
/// by f64-canonical row content. Per A0.
/// </summary>
internal sealed partial class ModalityBasisPass : IModelAnalysisPass
{
    public string PassId => "model.modality_basis";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public ModalityBasisPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        long totalEmitted = 0;
        long totalSkipped = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsModalityProjectionRole(t.Classification.Role))
            {
                continue;
            }

            (long emitted, long skipped) = t.Info.Shape.Length switch
            {
                2 => await PerRowContentPass.RunPerRowAsync(
                        context, session, t,
                        canonicalKindTag4: "mbas",
                        entityTypeCode: "modality_basis_vector",
                        edgeTypeCode: "has_modality_basis",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
                _ => await PerRowContentPass.RunPerOuterIndexAsync(
                        context, session, t,
                        canonicalKindTag4: "mbas",
                        entityTypeCode: "modality_basis_vector",
                        edgeTypeCode: "has_modality_basis",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
            };

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static bool IsModalityProjectionRole(TensorRole role) => role switch
    {
        TensorRole.VisionProjection => true,
        TensorRole.ModalityProjection => true,
        _ => false,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[modality-basis {ModelId}] complete — {TotalEmitted} basis vectors, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
