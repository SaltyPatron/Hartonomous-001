using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hartonomous.Integration.Tests.Orchestration;

/// <summary>
/// Asserts that <see cref="SequentialPhaseRunner"/> actually writes to
/// <c>monitor.phase_status</c> when given a real <see cref="NpgsqlDataSource"/>.
/// Regression coverage for a prior failure mode where the CLI was creating
/// runners without a data source (the ctor param defaulted to null),
/// silently suppressing every PersistStatusAsync call.
/// </summary>
public sealed class PhaseStatusPersistenceTests : IAsyncLifetime
{
    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
        ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous";

    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync()
    {
        _ds = NpgsqlDataSource.Create(ConnectionString());
        await ResetPhaseStatusAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await ResetPhaseStatusAsync(CancellationToken.None);
        await _ds.DisposeAsync();
    }

    [Fact]
    public async Task RunPhase_NoDecomposers_PersistsCompletedRow()
    {
        SequentialPhaseRunner runner = new(
            new Dictionary<Phase, IReadOnlyList<IDecomposer>>(),
            new FakePipeline(),
            new FakeReporter(),
            NullLogger<SequentialPhaseRunner>.Instance,
            new NpgsqlSessionStore(_ds));

        await runner.HydrateStatusAsync(CancellationToken.None);
        PhaseResult result = await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal(PhaseStatus.Completed, result.Status);

        (string status, string? err) = await ReadPhaseStatusAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal("completed", status);
        Assert.Null(err);
    }

    [Fact]
    public async Task RunPhase_DecomposerThrows_PersistsFailedRowWithMessage()
    {
        Dictionary<Phase, IReadOnlyList<IDecomposer>> map = new()
        {
            [Phase.CoreAlgebra] = [new ThrowingDecomposer("boom")],
        };
        SequentialPhaseRunner runner = new(
            map,
            new FakePipeline(),
            new FakeReporter(),
            NullLogger<SequentialPhaseRunner>.Instance,
            new NpgsqlSessionStore(_ds));

        await runner.HydrateStatusAsync(CancellationToken.None);
        PhaseResult result = await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal(PhaseStatus.Failed, result.Status);

        (string status, string? err) = await ReadPhaseStatusAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal("failed", status);
        Assert.Equal("boom", err);
    }

    [Fact]
    public async Task Hydrate_ReadsPersistedCompletion_ShortCircuitsOnRerun()
    {
        // First runner: complete CoreAlgebra, persist row.
        SequentialPhaseRunner first = new(
            new Dictionary<Phase, IReadOnlyList<IDecomposer>>(),
            new FakePipeline(),
            new FakeReporter(),
            NullLogger<SequentialPhaseRunner>.Instance,
            new NpgsqlSessionStore(_ds));
        await first.HydrateStatusAsync(CancellationToken.None);
        await first.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);

        // Second runner simulating a fresh CLI invocation. Its decomposer
        // would throw if invoked — the short-circuit on Hydrated=Completed
        // status must prevent that.
        Dictionary<Phase, IReadOnlyList<IDecomposer>> mustNotRun = new()
        {
            [Phase.CoreAlgebra] = [new ThrowingDecomposer("should not have run")],
        };
        SequentialPhaseRunner second = new(
            mustNotRun,
            new FakePipeline(),
            new FakeReporter(),
            NullLogger<SequentialPhaseRunner>.Instance,
            new NpgsqlSessionStore(_ds));
        await second.HydrateStatusAsync(CancellationToken.None);
        PhaseResult result = await second.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);

        Assert.Equal(PhaseStatus.Completed, result.Status);
        Assert.Null(result.ErrorMessage);
    }

    private async Task ResetPhaseStatusAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "DELETE FROM monitor.phase_status WHERE phase_code = ANY($1)", conn);
        cmd.Parameters.AddWithValue(new[] { Phase.CoreAlgebra.ToString() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(string Status, string? Error)> ReadPhaseStatusAsync(Phase phase, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT status, error_message FROM monitor.phase_status WHERE phase_code = $1", conn);
        cmd.Parameters.AddWithValue(phase.ToString());
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            Assert.Fail($"No phase_status row for {phase}");
        }
        string status = reader.GetString(0);
        string? err = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (status, err);
    }

    private sealed class ThrowingDecomposer(string message) : IDecomposer
    {
        public string ProvenanceCode => "test";
        public string DisplayName => "throwing";
        public IReadOnlyList<Phase> Phases => [Phase.CoreAlgebra];

        public Task ValidateSourceAsync(CancellationToken ct) => Task.CompletedTask;

        public Task DecomposeAsync(IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct) =>
            throw new InvalidOperationException(message);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePipeline : IIngestionPipeline
    {
        public PipelineStats Stats => new();
        public IIngestionBatch CreateBatch() => new FakeBatch();
        public IIngestionBatch CreateBatch(string provenanceCode) => new FakeBatch();
        public Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct) => Task.CompletedTask;
        public Task PopulateEdgeTrajectoriesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PrimeAllSignificanceAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBatch : IIngestionBatch
    {
        public string ProvenanceCode => "test";
        public int EntityCount => 0;
        public int EdgeCount => 0;
        public EntityHandle AddEntity(byte[] hash, string entityTypeCode) => new(hash, entityTypeCode);
        public void AddEdge(string edgeTypeCode, string provenanceCode, ReadOnlySpan<EdgeMemberSpec> members) { }
        public void AddJunction(string junctionTable, EntityHandle entity, int referenceId, double? mu = null) { }
        public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geomWkb) { }
        public void AddPhysicalityPoint4d(EntityHandle entity, string physicalityTypeCode, double x1, double x2, double x3, double x4) { }
        public void AddPhysicalityLineString4d(EntityHandle entity, string physicalityTypeCode, ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices) { }
        public void AddSequence(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1) { }
        public void AddSignificance(EntityHandle entity, string contextTypeCode, double initialMu) { }
        public void AddEntityModelSource(EntityHandle entity, long modelSourceId) { }
    }

    private sealed class FakeReporter : IProgressReporter
    {
        public Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
    }
}
