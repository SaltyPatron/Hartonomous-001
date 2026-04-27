using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for DIFFUSION_BLOCK tensors. Each outer-index
/// component (per-block, per-timestep-band, per-rank slice depending on
/// the tensor's shape) emits one <c>diffusion_component</c> entity hashed
/// by f64-canonical content. Per A0 / A8.
///
/// 2-D tensors decompose per-row (rank components); 3-D and 4-D
/// tensors decompose per-outer-index (block / channel) with trailing
/// dims flattened.
/// </summary>
internal sealed partial class DiffusionComponentPass : IModelAnalysisPass
{
    public string PassId => "model.diffusion_components";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public DiffusionComponentPass(ILogger logger)
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

            if (t.Classification.Role != TensorRole.DiffusionBlock)
            {
                continue;
            }

            (long emitted, long skipped) = t.Info.Shape.Length switch
            {
                2 => await PerRowContentPass.RunPerRowAsync(
                        context, session, t,
                        canonicalKindTag4: "difc",
                        entityTypeCode: "diffusion_component",
                        edgeTypeCode: "has_diffusion_component",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
                _ => await PerRowContentPass.RunPerOuterIndexAsync(
                        context, session, t,
                        canonicalKindTag4: "difc",
                        entityTypeCode: "diffusion_component",
                        edgeTypeCode: "has_diffusion_component",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
            };

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[diffusion-component {ModelId}] complete — {TotalEmitted} components, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
