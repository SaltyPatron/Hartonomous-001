using Hartonomous.Engine.Operations;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// CLI-side <see cref="IPromptIngestion"/> stand-in for commands that take a
/// pre-computed seed hash via <c>--seed-hash</c> rather than a prompt string.
/// The Op's <c>ResolveSeedHashAsync</c> short-circuits when
/// <c>OperationRequest.SeedHash</c> is set, so this wrapper is never asked
/// to ingest anything in that path. It exists to satisfy the constructor
/// dependency without spinning up a full <see cref="StreamingIngestionPipeline"/>.
/// </summary>
internal sealed class InlineSeedPromptIngestion : IPromptIngestion
{
    private readonly byte[] _hash;

    public InlineSeedPromptIngestion(byte[] hash) => _hash = hash;

    public Task<byte[]> IngestAsync(string promptText, string provenanceCode, double trustMu, CancellationToken ct)
        => Task.FromResult(_hash);
}
