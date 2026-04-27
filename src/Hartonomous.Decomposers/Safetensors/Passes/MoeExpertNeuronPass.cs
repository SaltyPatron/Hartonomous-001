using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for MoE expert FFN tensors: MoeExpertGate,
/// MoeExpertUp, MoeExpertDown. Each row = one neuron's contribution
/// within one expert. Emitted as <c>moe_expert_neuron</c> entity per row,
/// hashed by f64-canonical row content.
///
/// Per docs/specs/decomposers/analysis-passes.md A0 (MoE row).
/// </summary>
internal sealed partial class MoeExpertNeuronPass : IModelAnalysisPass
{
    public string PassId => "model.moe_expert_neurons";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public MoeExpertNeuronPass(ILogger logger)
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

            if (!IsMoeExpertRole(t.Classification.Role))
            {
                continue;
            }

            (long emitted, long skipped) = await PerRowContentPass.RunPerRowAsync(
                context, session, t,
                canonicalKindTag4: "moen",
                entityTypeCode: "moe_expert_neuron",
                edgeTypeCode: "has_moe_neuron",
                sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                ct);

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static bool IsMoeExpertRole(TensorRole role) => role switch
    {
        TensorRole.MoeExpertGate => true,
        TensorRole.MoeExpertUp => true,
        TensorRole.MoeExpertDown => true,
        _ => false,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[moe-expert {ModelId}] complete — {TotalEmitted} expert neurons, {TotalSkipped} rows skipped sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
