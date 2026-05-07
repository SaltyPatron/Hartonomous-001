using Hartonomous.Cli.Bootstrap;

namespace Hartonomous.Integration.Tests.Bootstrap;

public sealed class BootstrapSqlLoaderTests : IDisposable
{
    private readonly string _tempRoot;

    public BootstrapSqlLoaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"hartonomous_bootstrap_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }
    }

    [Fact]
    public void LoadResolved_ExpandsNestedIncludesRelativeToSqlRoot()
    {
        string sqlRoot = Path.Combine(_tempRoot, "sql");
        string schemaRoot = Path.Combine(sqlRoot, "schema");
        Directory.CreateDirectory(Path.Combine(schemaRoot, "tables"));
        Directory.CreateDirectory(Path.Combine(schemaRoot, "seed"));

        string manifest = Path.Combine(schemaRoot, "bootstrap.sql");
        File.WriteAllText(manifest, "-- @include schema/tables/core.sql");
        File.WriteAllText(Path.Combine(schemaRoot, "tables", "core.sql"),
            "CREATE TABLE substrate.entity (hash bytea);" + Environment.NewLine
            + "-- @include schema/seed/entity_type.sql");
        File.WriteAllText(Path.Combine(schemaRoot, "seed", "entity_type.sql"),
            "INSERT INTO substrate.entity_type (code) VALUES ('codepoint');");

        string resolved = BootstrapSqlLoader.LoadResolved(manifest);

        Assert.Contains("CREATE TABLE substrate.entity", resolved);
        Assert.Contains("INSERT INTO substrate.entity_type", resolved);
        AssertNoUnresolvedIncludeDirective(resolved);
    }

    [Fact]
    public void LoadResolved_MissingInclude_Throws()
    {
        string sqlRoot = Path.Combine(_tempRoot, "sql");
        string schemaRoot = Path.Combine(sqlRoot, "schema");
        Directory.CreateDirectory(schemaRoot);
        string manifest = Path.Combine(schemaRoot, "bootstrap.sql");
        File.WriteAllText(manifest, "-- @include schema/missing.sql");

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(
            () => BootstrapSqlLoader.LoadResolved(manifest));
        Assert.Contains("schema", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing.sql", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadResolved_RealBootstrapManifest_HasNoRemainingIncludes()
    {
        string repoRoot = FindRepoRoot();
        string manifest = Path.Combine(repoRoot, "sql", "schema", "bootstrap.sql");

        string resolved = BootstrapSqlLoader.LoadResolved(manifest);

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        AssertNoUnresolvedIncludeDirective(resolved);
        Assert.Contains("CREATE SCHEMA", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE", resolved, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoUnresolvedIncludeDirective(string sql)
    {
        Assert.DoesNotContain(
            sql.Split('\n'),
            line => line.TrimStart().StartsWith("-- @include", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Hartonomous.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Hartonomous.slnx not found");
    }
}