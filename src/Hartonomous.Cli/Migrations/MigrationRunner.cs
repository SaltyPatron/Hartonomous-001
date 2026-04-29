using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Cli.Migrations;

internal sealed class MigrationRunner
{
    private readonly IMigrationStore _store;
    private readonly string _migrationsDir;

    public MigrationRunner(IMigrationStore store, string migrationsDir)
    {
        _store = store;
        _migrationsDir = migrationsDir;
    }

    public async Task<int> UpAsync(CancellationToken ct, bool allowDrift = false)
    {
        IReadOnlyList<Migration> migrations = Migration.Discover(_migrationsDir);

        IReadOnlyDictionary<int, AppliedMigrationRecord> applied = await _store.GetAppliedMigrationsAsync(ct);
        if (allowDrift)
        {
            // Caller has explicitly accepted that some applied-migration source
            // files have changed since they were applied. Print drift to stderr
            // so it's still visible (not silent), then continue with new
            // migrations. The DB-side function definitions remain whatever the
            // last CREATE OR REPLACE made them — typically still correct because
            // function bodies are CREATE OR REPLACE'd, not destructively altered.
            ReportDriftWithoutFailing(migrations, applied);
        }
        else
        {
            AssertNoDriftOrGap(migrations, applied);
        }

        int appliedCount = 0;
        foreach (Migration m in migrations)
        {
            if (applied.ContainsKey(m.Version))
            {
                continue;
            }
            string sql = m.ReadUp();
            string checksum = m.UpChecksum();
            await _store.ApplyMigrationAsync(sql, m.Version, m.Name, checksum, ct);
            appliedCount++;
            Console.WriteLine($"Applied {m.Version:D4} {m.Name}");
        }

        if (appliedCount == 0)
        {
            Console.WriteLine("Database is up to date.");
        }
        return appliedCount;
    }

    private static void ReportDriftWithoutFailing(IReadOnlyList<Migration> migrations, IReadOnlyDictionary<int, AppliedMigrationRecord> applied)
    {
        Dictionary<int, Migration> byVersion = new();
        foreach (Migration m in migrations)
        {
            byVersion[m.Version] = m;
        }
        bool anyDrift = false;
        foreach (AppliedMigrationRecord a in applied.Values)
        {
            if (!byVersion.TryGetValue(a.Version, out Migration? m))
            {
                Console.Error.WriteLine($"  WARN: applied migration {a.Version:D4} '{a.Name}' has no matching file on disk (--allow-drift).");
                anyDrift = true;
                continue;
            }
            string currentChecksum = m.UpChecksum();
            if (currentChecksum != a.Checksum)
            {
                Console.Error.WriteLine($"  WARN: checksum drift on {a.Version:D4} '{a.Name}': stored={a.Checksum[..16]}…, current={currentChecksum[..16]}… (--allow-drift).");
                anyDrift = true;
            }
        }
        if (anyDrift)
        {
            Console.Error.WriteLine("  WARN: --allow-drift in effect: proceeding past drifted migrations. DB-side function/table state is whatever last applied; verify substrate.health_summary() before treating as correct.");
        }
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

        IReadOnlyDictionary<int, AppliedMigrationRecord> applied = await _store.GetAppliedMigrationsAsync(ct);
        List<AppliedMigrationRecord> appliedList = applied.Values.OrderByDescending(a => a.Version).ToList();
        int rolledBack = 0;
        foreach (AppliedMigrationRecord a in appliedList)
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
            string sql = m.ReadDown();
            await _store.RollbackMigrationAsync(sql, m.Version, ct);
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
        IReadOnlyDictionary<int, AppliedMigrationRecord> applied = await _store.GetAppliedMigrationsAsync(ct);

        Console.WriteLine($"{"Version",-10}{"Name",-40}{"Applied",-10}{"Checksum OK",-14}");
        foreach (Migration m in migrations)
        {
            string label = applied.TryGetValue(m.Version, out AppliedMigrationRecord? a) ? "yes" : "no";
            string checksumOk = a is null ? "-" : (a.Checksum == m.UpChecksum() ? "yes" : "DRIFT");
            Console.WriteLine($"{m.Version:D4}      {m.Name,-40}{label,-10}{checksumOk,-14}");
        }
    }

    private static void AssertNoDriftOrGap(IReadOnlyList<Migration> migrations, IReadOnlyDictionary<int, AppliedMigrationRecord> applied)
    {
        Dictionary<int, Migration> byVersion = new();
        foreach (Migration m in migrations)
        {
            byVersion[m.Version] = m;
        }
        foreach (AppliedMigrationRecord a in applied.Values)
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
}
