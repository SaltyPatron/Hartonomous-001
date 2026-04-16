using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Cli.Migrations;

internal sealed class MigrationRunner
{
    private readonly string _connectionString;
    private readonly string _migrationsDir;

    public MigrationRunner(string connectionString, string migrationsDir)
    {
        _connectionString = connectionString;
        _migrationsDir = migrationsDir;
    }

    public async Task<int> UpAsync(CancellationToken ct)
    {
        IReadOnlyList<Migration> migrations = Migration.Discover(_migrationsDir);

        await using NpgsqlConnection conn = new(_connectionString);
        await conn.OpenAsync(ct);

        IReadOnlyDictionary<int, AppliedMigration> applied = await LoadAppliedIfExistsAsync(conn, ct);
        AssertNoDriftOrGap(migrations, applied);

        int appliedCount = 0;
        foreach (Migration m in migrations)
        {
            if (applied.ContainsKey(m.Version))
            {
                continue;
            }
            await ApplyOneAsync(conn, m, ct);
            appliedCount++;
            Console.WriteLine($"Applied {m.Version:D4} {m.Name}");
        }

        if (appliedCount == 0)
        {
            Console.WriteLine("Database is up to date.");
        }
        return appliedCount;
    }

    public async Task DownAsync(int steps, CancellationToken ct)
    {
        if (steps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "steps must be >= 1");
        }

        IReadOnlyList<Migration> migrations = Migration.Discover(_migrationsDir);
        Dictionary<int, Migration> byVersion = new();
        foreach (Migration m in migrations)
        {
            byVersion[m.Version] = m;
        }

        await using NpgsqlConnection conn = new(_connectionString);
        await conn.OpenAsync(ct);

        List<AppliedMigration> appliedList = (await LoadAppliedAsync(conn, ct)).Values.OrderByDescending(a => a.Version).ToList();
        int rolledBack = 0;
        foreach (AppliedMigration a in appliedList)
        {
            if (rolledBack >= steps)
            {
                break;
            }
            if (!byVersion.TryGetValue(a.Version, out Migration? m))
            {
                throw new InvalidOperationException(
                    $"Applied migration {a.Version:D4} '{a.Name}' has no matching files on disk.");
            }
            await RollbackOneAsync(conn, m, ct);
            rolledBack++;
            Console.WriteLine($"Rolled back {m.Version:D4} {m.Name}");
        }

        if (rolledBack == 0)
        {
            Console.WriteLine("Nothing to roll back.");
        }
    }

    public async Task StatusAsync(CancellationToken ct)
    {
        IReadOnlyList<Migration> migrations = Migration.Discover(_migrationsDir);
        await using NpgsqlConnection conn = new(_connectionString);
        await conn.OpenAsync(ct);
        IReadOnlyDictionary<int, AppliedMigration> applied = await LoadAppliedIfExistsAsync(conn, ct);

        Console.WriteLine($"{"Version",-10}{"Name",-40}{"Applied",-10}{"Checksum OK",-14}");
        foreach (Migration m in migrations)
        {
            string label = applied.TryGetValue(m.Version, out AppliedMigration? a) ? "yes" : "no";
            string checksumOk = a is null ? "-" : (a.Checksum == m.UpChecksum() ? "yes" : "DRIFT");
            Console.WriteLine($"{m.Version:D4}      {m.Name,-40}{label,-10}{checksumOk,-14}");
        }
    }

    private static async Task<IReadOnlyDictionary<int, AppliedMigration>> LoadAppliedIfExistsAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await SchemaVersionExistsAsync(conn, ct))
        {
            return new Dictionary<int, AppliedMigration>();
        }
        return await LoadAppliedAsync(conn, ct);
    }

    private static async Task<bool> SchemaVersionExistsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'substrate' AND table_name = 'schema_version'
            )";
        await using NpgsqlCommand cmd = new(sql, conn);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is bool b && b;
    }

    private static async Task<IReadOnlyDictionary<int, AppliedMigration>> LoadAppliedAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        Dictionary<int, AppliedMigration> applied = new();
        const string sql = "SELECT version, name, checksum FROM substrate.schema_version ORDER BY version";
        await using NpgsqlCommand cmd = new(sql, conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            applied.Add(reader.GetInt32(0), new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return applied;
    }

    private static void AssertNoDriftOrGap(IReadOnlyList<Migration> migrations, IReadOnlyDictionary<int, AppliedMigration> applied)
    {
        Dictionary<int, Migration> byVersion = new();
        foreach (Migration m in migrations)
        {
            byVersion[m.Version] = m;
        }
        foreach (AppliedMigration a in applied.Values)
        {
            if (!byVersion.TryGetValue(a.Version, out Migration? m))
            {
                throw new InvalidOperationException(
                    $"Applied migration {a.Version:D4} '{a.Name}' has no matching files on disk. Refusing to proceed.");
            }
            string currentChecksum = m.UpChecksum();
            if (currentChecksum != a.Checksum)
            {
                throw new InvalidOperationException(
                    $"Checksum drift on {a.Version:D4} '{a.Name}': stored={a.Checksum}, current={currentChecksum}. Refusing to proceed.");
            }
        }
    }

    private static async Task ApplyOneAsync(NpgsqlConnection conn, Migration m, CancellationToken ct)
    {
        string sql = m.ReadUp();
        string checksum = m.UpChecksum();

        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);
        await using (NpgsqlCommand cmd = new(sql, conn, tx))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
            INSERT INTO substrate.schema_version (version, name, checksum)
            VALUES (@v, @n, @c)";
        await using (NpgsqlCommand cmd = new(insertSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("v", m.Version);
            cmd.Parameters.AddWithValue("n", m.Name);
            cmd.Parameters.AddWithValue("c", checksum);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task RollbackOneAsync(NpgsqlConnection conn, Migration m, CancellationToken ct)
    {
        string sql = m.ReadDown();

        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);
        await using (NpgsqlCommand cmd = new(sql, conn, tx))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (m.Version > 1)
        {
            const string deleteSql = "DELETE FROM substrate.schema_version WHERE version = @v";
            await using NpgsqlCommand cmd = new(deleteSql, conn, tx);
            cmd.Parameters.AddWithValue("v", m.Version);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

}
