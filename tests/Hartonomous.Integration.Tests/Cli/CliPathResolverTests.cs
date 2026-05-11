using Hartonomous.Cli.Configuration;

namespace Hartonomous.Integration.Tests.Cli;

public sealed class CliPathResolverTests
{
    [Fact]
    public void Resolve_NormalizesWindowsRelativeSegmentsOnLinux()
    {
        string path = CliPathResolver.Resolve("/vault/Data", "Unicode\\Public\\UCD\\latest");

        Assert.Equal(Path.Combine("/vault/Data", "Unicode", "Public", "UCD", "latest"), path);
    }

    [Fact]
    public void Resolve_UsesAbsolutePathAsConfigured()
    {
        string path = CliPathResolver.Resolve("/vault/Data", "/vault/Data/ISO639");

        Assert.Equal("/vault/Data/ISO639", path);
    }
}
