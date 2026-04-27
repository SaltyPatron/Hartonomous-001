using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for VISION_FEATURE tensors. Each row is the
/// learned direction for one patch position in the vision encoder's
/// feature space. Hashed by f64-canonical row content. Per A0.
/// </summary>
internal sealed partial class VisionFeaturePass : IModelAnalysisPass
{
    public string PassId => "model.vision_features";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public VisionFeaturePass(ILogger logger)
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

            if (t.Classification.Role != TensorRole.VisionFeature)
            {
                continue;
            }

            // 4-D conv-style vision tensors are flattened on trailing dims;
            // 2-D feature tables (patch × hidden) use the per-row path.
            (long emitted, long skipped) = t.Info.Shape.Length switch
            {
                2 => await PerRowContentPass.RunPerRowAsync(
                        context, session, t,
                        canonicalKindTag4: "vsft",
                        entityTypeCode: "vision_feature_direction",
                        edgeTypeCode: "has_vision_feature",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
                _ => await PerRowContentPass.RunPerOuterIndexAsync(
                        context, session, t,
                        canonicalKindTag4: "vsft",
                        entityTypeCode: "vision_feature_direction",
                        edgeTypeCode: "has_vision_feature",
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
        [LoggerMessage(Level = LogLevel.Information, Message = "[vision-feature {ModelId}] complete — {TotalEmitted} feature directions, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
