using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Integration.Tests.SchemaDrift;

/// <summary>
/// Schema-drift lint. Scans every C# source file under <c>src/</c> and every
/// SQL file under <c>sql/migrations/</c> + <c>sql/schema/{functions,procedures,views,tables}/</c>
/// for tokens of the form <c>substrate.&lt;ident&gt;</c>, then checks every unique
/// reference against the live migrated database. A reference is considered
/// satisfied when the identifier exists as one of:
///   - relation (table, view, materialised view, foreign table, partition) in <c>pg_class</c>
///   - function or procedure in <c>pg_proc</c>
///   - composite type / domain / enum in <c>pg_type</c>
///
/// Hits on commented lines (line begins with <c>//</c> or <c>--</c> after
/// optional whitespace) are skipped. Strings / SQL bodies inside
/// <c>$$ ... $$</c> dollar-quoted blocks are scanned the same as the rest of
/// the file.
/// </summary>
[Trait("Category", "SchemaDrift")]
public sealed class SubstrateReferenceLintTests
{
    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
        ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous";

    /// <summary>
    /// Allowlist of <c>substrate.&lt;name&gt;</c> tokens that legitimately do NOT
    /// resolve to a database object — typically PG built-in or extension namespace
    /// collisions, schema names appearing in DDL, or doc-only mentions. Empty by
    /// default; entries belong here only with a comment justifying the exception.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Schema name itself, used in DDL like CREATE SCHEMA substrate;
        "substrate",
    };

    /// <summary>
    /// Identifiers that are columns / parameters that happen to match
    /// <c>substrate.something</c> when scanned naively (e.g.
    /// <c>substrate.entity.entity_type_id</c> picks up <c>entity_type_id</c>
    /// only via the column-qualifier path). We resolve to the table/view name
    /// only — the lint is at the schema-object level, not column.
    /// </summary>
    private static readonly Regex Token = new(
        @"\bsubstrate\.([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    [Fact]
    public async Task EveryReferenceResolvesToALiveDatabaseObject()
    {
        string repoRoot = FindRepoRoot();

        List<Reference> references = new();
        ScanCSharp(Path.Combine(repoRoot, "src"), references);
        ScanSql(Path.Combine(repoRoot, "sql", "migrations"), references);
        ScanSql(Path.Combine(repoRoot, "sql", "schema", "functions"), references);
        ScanSql(Path.Combine(repoRoot, "sql", "schema", "procedures"), references);
        ScanSql(Path.Combine(repoRoot, "sql", "schema", "views"), references);
        ScanSql(Path.Combine(repoRoot, "sql", "schema", "tables"), references);

        Assert.True(references.Count > 0,
            "Lint did not find any substrate.* references; scan paths or regex are wrong.");

        HashSet<string> uniqueNames = new(
            references.Select(r => r.Name).Where(n => !Allowlist.Contains(n)),
            StringComparer.OrdinalIgnoreCase);

        HashSet<string> live = await LoadLiveSubstrateObjectsAsync();

        List<Reference> dead = references
            .Where(r => !Allowlist.Contains(r.Name) && !live.Contains(r.Name))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line)
            .ToList();

        if (dead.Count > 0)
        {
            string body = string.Join("\n",
                dead.Select(r => $"  substrate.{r.Name}  ({Path.GetRelativePath(repoRoot, r.File)}:{r.Line})"));
            Assert.Fail(
                $"Found {dead.Count} substrate.* references with no matching live database object:\n{body}");
        }
    }

    private static void ScanCSharp(string root, List<Reference> sink)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // Skip generated obj/ output that may live under bin/obj inside src.
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ScanFile(path, sink, CommentPrefix.CSharp);
        }
    }

    private static void ScanSql(string root, List<Reference> sink)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (string path in Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories))
        {
            ScanFile(path, sink, CommentPrefix.Sql);
        }
    }

    private static void ScanFile(string path, List<Reference> sink, CommentPrefix commentStyle)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            // Skip pure-comment lines for the lint. In SQL we keep -- ; in C# we keep // and ///.
            // We don't try to parse multi-line block comments — the regex still hits inside them
            // but the cost is at most a couple of false negatives in the dead-list, never false
            // positives.
            if (commentStyle == CommentPrefix.CSharp && (trimmed.StartsWith("//", StringComparison.Ordinal)))
            {
                continue;
            }
            if (commentStyle == CommentPrefix.Sql && trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match m in Token.Matches(line))
            {
                string name = m.Groups[1].Value;
                sink.Add(new Reference(name, path, i + 1));
            }
        }
    }

    private static async Task<HashSet<string>> LoadLiveSubstrateObjectsAsync()
    {
        HashSet<string> objects = new(StringComparer.OrdinalIgnoreCase);

        await using NpgsqlConnection conn = new(ConnectionString());
        await conn.OpenAsync();

        // pg_class: tables, views, materialized views, partitions, sequences, foreign tables.
        const string ClassSql = @"
            SELECT c.relname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'substrate'";
        await using (NpgsqlCommand cmd = new(ClassSql, conn))
        await using (NpgsqlDataReader rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                objects.Add(rd.GetString(0));
            }
        }

        // pg_proc: functions and procedures.
        const string ProcSql = @"
            SELECT p.proname
              FROM pg_proc p
              JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'substrate'";
        await using (NpgsqlCommand cmd = new(ProcSql, conn))
        await using (NpgsqlDataReader rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                objects.Add(rd.GetString(0));
            }
        }

        // pg_type: composite types, domains, enums.
        const string TypeSql = @"
            SELECT t.typname
              FROM pg_type t
              JOIN pg_namespace n ON n.oid = t.typnamespace
             WHERE n.nspname = 'substrate'";
        await using (NpgsqlCommand cmd = new(TypeSql, conn))
        await using (NpgsqlDataReader rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                objects.Add(rd.GetString(0));
            }
        }

        return objects;
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

    private enum CommentPrefix { CSharp, Sql }

    private sealed record Reference(string Name, string File, int Line);
}
