using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Phase A — bind tokenizer tokens to seed POS / morph_feature evidence.
///
/// VocabCoveragePass connects bpe_token entities (from TokenizerMappingPass)
/// to substrate lemma entities (from WordNet / UD / Wiktionary) via
/// <c>covers_lemma</c> edges. This pass walks those edges and propagates
/// the lemma's POS classifications and morphological features onto the
/// bpe_token via <c>entity_pos</c> and <c>entity_morph_feature</c>
/// junctions. The result: a query "all model tokens classified as a noun"
/// works uniformly across model and seed evidence.
///
/// Per the corrected build plan task #71. The richer interpretation in
/// docs/specs/decomposers/analysis-passes.md (attention-archetype
/// clustering → grammar fragment synthesis) is a future refinement; the
/// binding pass here is the substrate's foundational integration of the
/// model's vocabulary with the seed lexicon's grammatical taxonomy.
///
/// Depends on: TokenizerMappingPass + VocabCoveragePass.
/// </summary>
internal sealed partial class GrammarExtractionPass : IModelAnalysisPass
{
    public string PassId => "model.grammar_extraction";
    public IReadOnlyList<string> Dependencies => ["model.tokenizer", "model.vocab_coverage"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;
    private readonly NpgsqlDataSource? _dataSource;

    public GrammarExtractionPass(ILogger logger, NpgsqlDataSource? dataSource = null)
    {
        _logger = logger;
        _dataSource = dataSource;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        if (_dataSource is null)
        {
            Log.NoDataSource(_logger, context.Source.ModelId);
            return;
        }

        long posBindings = await CallBindAsync(
            "SELECT substrate.bind_bpe_tokens_to_seed_pos($1)",
            context.Source.ModelSourceId, ct);

        long morphBindings = await CallBindAsync(
            "SELECT substrate.bind_bpe_tokens_to_seed_morph($1)",
            context.Source.ModelSourceId, ct);

        Log.PassComplete(_logger, context.Source.ModelId, posBindings, morphBindings);
    }

    private async Task<long> CallBindAsync(string sql, long modelSourceId, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = modelSourceId });
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[grammar-extraction {ModelId}] no NpgsqlDataSource injected — binding skipped (composition root must wire it)")]
        public static partial void NoDataSource(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[grammar-extraction {ModelId}] complete — bound {PosBindings} entity_pos rows + {MorphBindings} entity_morph_feature rows from seed lexicon")]
        public static partial void PassComplete(ILogger logger, string modelId, long posBindings, long morphBindings);
    }
}
