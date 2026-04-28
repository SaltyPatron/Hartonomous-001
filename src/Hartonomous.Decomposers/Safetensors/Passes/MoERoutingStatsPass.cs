using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// MoE router weight analysis: per-layer expert utilization (fraction of router
/// scores flowing to each expert under uniform input), routing entropy, and
/// dead-expert detection. Static weight inspection only; no token-routing
/// simulation. The router weight matrix is shape [num_experts, hidden_size];
/// we approximate per-expert utilization as the L2 norm of each expert's row,
/// normalized to a probability distribution.
///
/// Entity: <c>moe_routing_profile</c> per layer. Signature: model_architecture
/// hash + layer index + expert count + utilization vector packed f64-LE.
/// Layer index IS content for this entity (a routing profile belongs to a
/// specific layer of a specific architecture).
///
/// Per docs/specs/decomposers/analysis-passes.md § "MoERoutingStatsPass".
/// </summary>
internal sealed partial class MoERoutingStatsPass : IModelAnalysisPass
{
    public string PassId => "model.moe_routing";
    public IReadOnlyList<string> Dependencies => [];

    // MoE-only — empty list would apply to every architecture and emit useless
    // entities. The orchestrator skips passes whose architecture filter excludes
    // the current model.
    public IReadOnlyList<string> AppliesToArchitectures =>
    [
        "DeepseekV2ForCausalLM", "DeepseekV3ForCausalLM",
        "Qwen2MoeForCausalLM", "Qwen3MoeForCausalLM",
        "MixtralForCausalLM",
    ];

    private readonly ILogger _logger;

    public MoERoutingStatsPass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();

            if (t.Classification.Role != TensorRole.MoeRouter)
            {
                continue;
            }
            int? layer = t.Classification.LayerIndex;
            if (layer is null)
            {
                Log.MissingLayerIndex(_logger, t.Info.Name);
                continue;
            }
            if (t.Info.Shape.Length != 2)
            {
                Log.WrongShape(_logger, t.Info.Name, t.Info.Shape.Length);
                continue;
            }

            int experts = (int)t.Info.Shape[0];
            int hidden = (int)t.Info.Shape[1];
            double[] sumSqPerExpert = new double[experts];

            // Stream rows of the router matrix; element at flat index k
            // belongs to expert (k / hidden).
            long globalIndex = 0;
            SafetensorsReader.StreamDecode(t.Info, chunk =>
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    int expert = (int)((globalIndex + i) / hidden);
                    if (expert < experts)
                    {
                        double v = chunk[i];
                        sumSqPerExpert[expert] += v * v;
                    }
                }
                globalIndex += chunk.Length;
            });

            double totalNorm = 0;
            for (int e = 0; e < experts; e++)
            {
                sumSqPerExpert[e] = Math.Sqrt(sumSqPerExpert[e]);
                totalNorm += sumSqPerExpert[e];
            }
            double[] utilization = new double[experts];
            int deadExperts = 0;
            double entropy = 0;
            if (totalNorm > 0)
            {
                for (int e = 0; e < experts; e++)
                {
                    utilization[e] = sumSqPerExpert[e] / totalNorm;
                    if (utilization[e] < 1e-9)
                    {
                        deadExperts++;
                    }
                    else
                    {
                        entropy -= utilization[e] * Math.Log(utilization[e]);
                    }
                }
            }

            // AP-9: hash covers ROUTING-PROFILE CONTENT only (expert count +
            // utilization vector). Architecture and layer are PLACEMENT
            // metadata — they live on the has_moe_routing edge and
            // entity_model_source linkage, never inside the identity hash.
            // Same routing profile across different (model, layer) positions
            // collapses to ONE moe_routing_profile entity with multiple
            // incoming edges — cross-model corroboration on routing patterns.
            ICanonicalSignatureBuilder b = context.NewSignature("moer")
                .WriteInt32LE(experts);
            for (int e = 0; e < experts; e++)
            {
                b.WriteDouble(utilization[e]);
            }
            byte[] hash = b.Finalize();

            EntityHandle profile = session.Batch.AddEntity(hash, "moe_routing_profile");
            session.Batch.AddEntityModelSource(profile, context.Source.ModelSourceId);
            session.Batch.AddEdge("has_moe_routing", context.ProvenanceCode,
            [
                new EdgeMemberSpec(t.Entity, "source", 0),
                new EdgeMemberSpec(profile, "target", 1),
            ]);

            Log.LayerProfiled(_logger, layer.Value, experts, deadExperts, entropy);
        }
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[moe layer={Layer}] experts={Experts} dead={Dead} entropy={Entropy:F3}")]
        public static partial void LayerProfiled(ILogger logger, int layer, int experts, int dead, double entropy);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[moe] {Name} missing layer index; skipped")]
        public static partial void MissingLayerIndex(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[moe] {Name} expected rank-2 router matrix, got {Rank}; skipped")]
        public static partial void WrongShape(ILogger logger, string name, int rank);
    }
}
