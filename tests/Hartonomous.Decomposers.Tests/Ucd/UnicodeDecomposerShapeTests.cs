using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Orchestration;
using Hartonomous.Decomposers.Ucd;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hartonomous.Decomposers.Tests.Ucd;

/// <summary>
/// Surface-shape tests for the consolidated UnicodeDecomposer. Verifies the
/// producer-pattern contract (ProvenanceCode, Phases, source path discovery)
/// without spinning up Postgres or the UCD source tree. Behavioral coverage
/// runs through the Integration / Conformance suites against live UCD data.
/// </summary>
public sealed class UnicodeDecomposerShapeTests
{
    [Fact]
    public void ProvenanceCode_IsUnicodeConsortium()
    {
        UnicodeDecomposer decomposer = NewDecomposer("/vault/Data");
        Assert.Equal("unicode_consortium", decomposer.ProvenanceCode);
    }

    [Fact]
    public void Phases_ContainsUcdUca()
    {
        UnicodeDecomposer decomposer = NewDecomposer("/vault/Data");
        Assert.Contains(Phase.UcdUca, decomposer.Phases);
    }

    [Fact]
    public void DisplayName_NonEmpty()
    {
        UnicodeDecomposer decomposer = NewDecomposer("/vault/Data");
        Assert.False(string.IsNullOrWhiteSpace(decomposer.DisplayName));
    }

    [Fact]
    public async Task ValidateSourceAsync_MissingSourceDirectory_LogsAndReturns()
    {
        // The decomposer logs (does not throw) when the source directory is
        // missing — matches the prior UcdUcaDecomposer contract where the
        // extension catalog can carry the materializer through to completion
        // when source files are absent (degraded but non-fatal).
        UnicodeDecomposer decomposer = NewDecomposer(
            Path.Combine(Path.GetTempPath(), "hartonomous-missing-ucd-source"));

        await decomposer.ValidateSourceAsync(CancellationToken.None);
    }

    private static UnicodeDecomposer NewDecomposer(string sourceDirectory)
    {
        DecomposerConfig cfg = new()
        {
            SourceDirectory = sourceDirectory,
            ConnectionString = "Host=localhost;Database=hartonomous",
        };
        return new UnicodeDecomposer(cfg, NullLogger<UnicodeDecomposer>.Instance);
    }
}
