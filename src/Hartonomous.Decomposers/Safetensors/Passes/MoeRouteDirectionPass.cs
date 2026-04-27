using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for MoE router tensors. Each row of a MoeRouter
/// tensor is the direction in residual-stream space that selects one
/// expert. Emitted as <c>moe_route_direction</c> entity per row, hashed
/// by f64-canonical row content.
///
/// Per docs/specs/decomposers/analysis-passes.md A7.
/// </summary>
internal sealed partial class MoeRouteDirectionPass : IModelAnalysisPass
{
    public string PassId => "model.moe_route_directions";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public MoeRouteDirectionPass(ILogger logger)
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

            if (t.Classification.Role != TensorRole.MoeRouter)
            {
                continue;
            }

            (long emitted, long skipped) = await PerRowContentPass.RunPerRowAsync(
                context, session, t,
                canonicalKindTag4: "mort",
                entityTypeCode: "moe_route_direction",
                edgeTypeCode: "has_route_direction",
                sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                ct);

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[moe-router {ModelId}] complete — {TotalEmitted} route directions, {TotalSkipped} rows skipped sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
