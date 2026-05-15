using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Decomposers.Ucd;

public sealed partial class UcdUcaDecomposer : BaseDecomposer
{
    private readonly string _connectionString;
    private readonly string _sourceDirectory;
    private readonly UnicodePassOrchestrator _orchestrator;

    public UcdUcaDecomposer(DecomposerConfig config, ILogger<UcdUcaDecomposer> logger)
        : base(config, logger)
    {
        _connectionString = config.ConnectionString;
        _sourceDirectory = config.SourceDirectory;
        _orchestrator = new UnicodePassOrchestrator(CreatePasses(), logger);
    }

    public override string ProvenanceCode => "unicode_consortium";

    public override string DisplayName => "UCD/UCA foundational seed materializer";

    public override IReadOnlyList<Phase> Phases => [Phase.UcdUca];

    protected override IReadOnlyList<string> GetSourcePaths()
        => string.IsNullOrWhiteSpace(_sourceDirectory) ? [] : [_sourceDirectory];

    public override Task ValidateSourceAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_sourceDirectory) && !Path.Exists(_sourceDirectory))
        {
            Log.SourceDirectoryNotFound(Logger, _sourceDirectory);
        }

        return Task.CompletedTask;
    }

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        await pipeline.DrainPendingAsync(ct);

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(_connectionString);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct);

        UnicodePassContext context = new(
            dataSource,
            connection,
            reporter,
            ProvenanceCode,
            _sourceDirectory,
            Logger);
        await _orchestrator.RunAsync(context, ct);
        Log.Materialized(Logger);
    }

    private static IReadOnlyList<IUnicodeSeedPass> CreatePasses()
        =>
        [
            new ExtensionCatalogVerificationPass(),
            new UnicodeReferenceVocabularyPass(),
            new CodepointAtomPass(),
            new CodepointPropertyPass(),
            new UnicodeCaseEdgePass(),
            new UnicodeDecompositionEdgePass(),
            new UnicodeFullCaseMappingEdgePass(),
            new UnicodeConfusablePass(),
            new UnicodeStandardizedVariantPass(),
            new UnicodeRadicalStrokePass(),
            new UnicodeNamedSequencePass(),
            new UnicodeEmojiSequencePass(),
            new UnicodeMaterializationValidationPass(),
        ];

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "UCD/UCA source directory not found; current materializer will use the installed extension catalog: {Path}")]
        public static partial void SourceDirectoryNotFound(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Information, Message = "UCD/UCA materialization completed")]
        public static partial void Materialized(ILogger logger);
    }
}
