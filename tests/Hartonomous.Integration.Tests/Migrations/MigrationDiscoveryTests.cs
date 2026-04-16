using Hartonomous.Cli.Migrations;

namespace Hartonomous.Integration.Tests.Migrations;

public sealed class MigrationDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public MigrationDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hartonomous_mig_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void Discover_EmptyDirectory_ReturnsEmpty()
    {
        IReadOnlyList<Migration> migrations = Migration.Discover(_tempDir);
        Assert.Empty(migrations);
    }

    [Fact]
    public void Discover_SingleMigration_Parsed()
    {
        WritePair(1, "initial_schema", "CREATE TABLE t;", "DROP TABLE t;");

        IReadOnlyList<Migration> migrations = Migration.Discover(_tempDir);

        Assert.Single(migrations);
        Assert.Equal(1, migrations[0].Version);
        Assert.Equal("initial_schema", migrations[0].Name);
    }

    [Fact]
    public void Discover_MultipleMigrations_OrderedByVersion()
    {
        WritePair(3, "indexes", "CREATE INDEX;", "DROP INDEX;");
        WritePair(1, "schema", "CREATE TABLE;", "DROP TABLE;");
        WritePair(2, "data", "INSERT;", "DELETE;");

        IReadOnlyList<Migration> migrations = Migration.Discover(_tempDir);

        Assert.Equal(3, migrations.Count);
        Assert.Equal(1, migrations[0].Version);
        Assert.Equal(2, migrations[1].Version);
        Assert.Equal(3, migrations[2].Version);
    }

    [Fact]
    public void Discover_NumberingGap_Throws()
    {
        WritePair(1, "schema", "CREATE;", "DROP;");
        WritePair(3, "indexes", "CREATE;", "DROP;"); // skipped 2

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Migration.Discover(_tempDir));
        Assert.Contains("gap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_MissingDownFile_Throws()
    {
        File.WriteAllText(Path.Combine(_tempDir, "0001_schema.up.sql"), "CREATE;");
        // No corresponding .down.sql

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Migration.Discover(_tempDir));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_NonexistentDirectory_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => Migration.Discover(Path.Combine(_tempDir, "nonexistent")));
    }

    [Fact]
    public void ReadUp_ReturnsFileContent()
    {
        string sql = "CREATE TABLE test (id INT);";
        WritePair(1, "schema", sql, "DROP TABLE test;");

        IReadOnlyList<Migration> migrations = Migration.Discover(_tempDir);
        Assert.Equal(sql, migrations[0].ReadUp());
    }

    [Fact]
    public void ReadDown_ReturnsFileContent()
    {
        string downSql = "DROP TABLE test;";
        WritePair(1, "schema", "CREATE TABLE test;", downSql);

        IReadOnlyList<Migration> migrations = Migration.Discover(_tempDir);
        Assert.Equal(downSql, migrations[0].ReadDown());
    }

    [Fact]
    public void UpChecksum_DeterministicForSameContent()
    {
        WritePair(1, "schema", "CREATE TABLE test;", "DROP TABLE test;");

        IReadOnlyList<Migration> migrations = Migration.Discover(_tempDir);
        string checksum1 = migrations[0].UpChecksum();
        string checksum2 = migrations[0].UpChecksum();

        Assert.Equal(checksum1, checksum2);
        Assert.Equal(64, checksum1.Length); // SHA-256 hex
    }

    [Fact]
    public void UpChecksum_DifferentContentProducesDifferentChecksum()
    {
        WritePair(1, "schema", "CREATE TABLE a;", "DROP TABLE a;");
        WritePair(2, "data", "CREATE TABLE b;", "DROP TABLE b;");

        IReadOnlyList<Migration> migrations = Migration.Discover(_tempDir);
        Assert.NotEqual(migrations[0].UpChecksum(), migrations[1].UpChecksum());
    }

    [Fact]
    public void Discover_RealMigrations_AllParsedAndOrdered()
    {
        string repoRoot = FindRepoRoot();
        string migrationsDir = Path.Combine(repoRoot, "sql", "migrations");

        if (!Directory.Exists(migrationsDir))
        {
            return;
        }

        IReadOnlyList<Migration> migrations = Migration.Discover(migrationsDir);

        Assert.True(migrations.Count >= 15, $"Expected >=15 migrations, got {migrations.Count}");

        // Verify sequential numbering.
        for (int i = 0; i < migrations.Count; i++)
        {
            Assert.Equal(i + 1, migrations[i].Version);
        }

        // Every migration has readable up/down content.
        foreach (Migration m in migrations)
        {
            string up = m.ReadUp();
            string down = m.ReadDown();
            Assert.False(string.IsNullOrWhiteSpace(up), $"Migration {m.Version:D4} has empty up SQL");
            Assert.False(string.IsNullOrWhiteSpace(down), $"Migration {m.Version:D4} has empty down SQL");
        }

        // Checksums are stable.
        foreach (Migration m in migrations)
        {
            string cs = m.UpChecksum();
            Assert.Equal(64, cs.Length);
            Assert.True(string.Equals(cs, cs.ToLowerInvariant(), StringComparison.Ordinal), "Checksum should be lowercase hex");
        }
    }

    [Fact]
    public void Discover_IgnoresNonMigrationFiles()
    {
        WritePair(1, "schema", "CREATE;", "DROP;");
        File.WriteAllText(Path.Combine(_tempDir, "README.md"), "not a migration");
        File.WriteAllText(Path.Combine(_tempDir, "notes.sql"), "not a migration");

        IReadOnlyList<Migration> migrations = Migration.Discover(_tempDir);
        Assert.Single(migrations);
    }

    private void WritePair(int version, string name, string upSql, string downSql)
    {
        File.WriteAllText(Path.Combine(_tempDir, $"{version:D4}_{name}.up.sql"), upSql);
        File.WriteAllText(Path.Combine(_tempDir, $"{version:D4}_{name}.down.sql"), downSql);
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
