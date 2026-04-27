using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for LoRA adapter tensors. LORA_A and LORA_B are
/// rank-limited adapters by construction — each row is one rank component.
/// Hashed by f64-canonical row content. Per A0 / A9.
///
/// No sparsity filter: rank-limited tensors are dense by design; every
/// component is kept. Same row content across LoRA adapters → ONE entity
/// → cross-adapter Glicko-2 corroboration on rank components.
/// </summary>
internal sealed partial class LoraComponentPass : IModelAnalysisPass
{
    public string PassId => "model.lora_components";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public LoraComponentPass(ILogger logger)
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

            if (!IsLoraRole(t.Classification.Role))
            {
                continue;
            }

            (long emitted, long skipped) = await PerRowContentPass.RunPerRowAsync(
                context, session, t,
                canonicalKindTag4: "lorc",
                entityTypeCode: "lora_component",
                edgeTypeCode: "has_lora_component",
                sparsityThreshold: 0.0,
                flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                ct);

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static bool IsLoraRole(TensorRole role) => role switch
    {
        TensorRole.LoraA => true,
        TensorRole.LoraB => true,
        _ => false,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[lora-component {ModelId}] complete — {TotalEmitted} rank components, {TotalSkipped} skipped")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
