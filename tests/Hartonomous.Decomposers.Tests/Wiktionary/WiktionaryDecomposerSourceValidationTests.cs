using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Errors;
using Hartonomous.Core.Text;
using Hartonomous.Decomposers.Wiktionary;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hartonomous.Decomposers.Tests.Wiktionary;

public sealed class WiktionaryDecomposerSourceValidationTests
{
    [Fact]
    public async Task ValidateSourceAsync_MissingJsonl_FailsLoudlyWithCandidates()
    {
        string missingRoot = Path.Combine(Path.GetTempPath(), "hartonomous-missing-wiktionary-jsonl");
        WiktionaryDecomposer decomposer = new(
            new DecomposerConfig
            {
                SourceDirectory = missingRoot,
                ConnectionString = "Host=localhost;Database=hartonomous",
            },
            new SubstrateTextDecomposer(),
            NullLogger<WiktionaryDecomposer>.Instance,
            codepointProperties: null!);

        SourceValidationException ex = await Assert.ThrowsAsync<SourceValidationException>(
            () => decomposer.ValidateSourceAsync(CancellationToken.None));

        Assert.Contains("Wiktionary JSONL source not found", ex.Message);
        Assert.Contains("kaikki.org-dictionary-English.jsonl", ex.Message);
        Assert.Contains("raw-wiktextract-data.jsonl", ex.Message);
    }
}
