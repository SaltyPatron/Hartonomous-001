using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
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

        (string status, DateTime? startedAt, DateTime? completedAt, string? err) = await ReadPhaseStatusAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal("completed", status);
        Assert.NotNull(startedAt);
        Assert.NotNull(completedAt);
        Assert.Null(err);
    }

    [Fact]
    public async Task RunPhase_RequiredPhaseNoDecomposers_PersistsFailedRow()
    {
        SequentialPhaseRunner runner = new(
            new Dictionary<Phase, IReadOnlyList<IDecomposer>>(),
            new FakePipeline(),
            new FakeReporter(),
            NullLogger<SequentialPhaseRunner>.Instance,
            new NpgsqlSessionStore(_ds));

        await runner.HydrateStatusAsync(CancellationToken.None);
        PhaseResult core = await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal(PhaseStatus.Completed, core.Status);

        PhaseResult result = await runner.RunPhaseAsync(Phase.UcdUca, CancellationToken.None);
        Assert.Equal(PhaseStatus.Failed, result.Status);

        (string status, DateTime? startedAt, DateTime? completedAt, string? err) = await ReadPhaseStatusAsync(Phase.UcdUca, CancellationToken.None);
        Assert.Equal("failed", status);
        Assert.NotNull(startedAt);
        Assert.NotNull(completedAt);
        Assert.Contains("no registered decomposer", err);
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

        (string status, DateTime? startedAt, DateTime? completedAt, string? err) = await ReadPhaseStatusAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal("failed", status);
        Assert.NotNull(startedAt);
        Assert.NotNull(completedAt);
        Assert.Equal("boom", err);
    }

    [Fact]
    public async Task UpdatePhaseStatus_RerunAfterFailure_ClearsStaleCompletionAndError()
    {
        NpgsqlSessionStore store = new(_ds);

        await store.UpdatePhaseStatusAsync(Phase.CoreAlgebra.ToString(), "running", null, CancellationToken.None);
        await store.UpdatePhaseStatusAsync(Phase.CoreAlgebra.ToString(), "failed", "boom", CancellationToken.None);

        (string failedStatus, DateTime? failedStartedAt, DateTime? failedCompletedAt, string? failedError) =
            await ReadPhaseStatusAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal("failed", failedStatus);
        Assert.NotNull(failedStartedAt);
        Assert.NotNull(failedCompletedAt);
        Assert.Equal("boom", failedError);

        await store.UpdatePhaseStatusAsync(Phase.CoreAlgebra.ToString(), "running", null, CancellationToken.None);

        (string runningStatus, DateTime? runningStartedAt, DateTime? runningCompletedAt, string? runningError) =
            await ReadPhaseStatusAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal("running", runningStatus);
        Assert.NotNull(runningStartedAt);
        Assert.Null(runningCompletedAt);
        Assert.Null(runningError);

        await store.UpdatePhaseStatusAsync(Phase.CoreAlgebra.ToString(), "completed", null, CancellationToken.None);

        (string completedStatus, DateTime? completedStartedAt, DateTime? completedCompletedAt, string? completedError) =
            await ReadPhaseStatusAsync(Phase.CoreAlgebra, CancellationToken.None);
        Assert.Equal("completed", completedStatus);
        Assert.NotNull(completedStartedAt);
        Assert.NotNull(completedCompletedAt);
        Assert.Null(completedError);
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
        cmd.Parameters.AddWithValue(new[] { Phase.CoreAlgebra.ToString(), Phase.UcdUca.ToString() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(string Status, DateTime? StartedAt, DateTime? CompletedAt, string? Error)> ReadPhaseStatusAsync(Phase phase, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _ds.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT status, started_at, completed_at, error_message FROM monitor.phase_status WHERE phase_code = $1", conn);
        cmd.Parameters.AddWithValue(phase.ToString());
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            Assert.Fail($"No phase_status row for {phase}");
        }
        string status = reader.GetString(0);
        DateTime? startedAt = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        DateTime? completedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        string? err = reader.IsDBNull(3) ? null : reader.GetString(3);
        return (status, startedAt, completedAt, err);
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
        public Task DrainPendingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PopulateSequencePhysicalityAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PopulateEdgeTrajectoriesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PrimeAllSignificanceAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<HashSet<HashKey>> GetExistingEntityHashesAsync(IReadOnlyCollection<Hash32> hashes, CancellationToken ct) => Task.FromResult(new HashSet<HashKey>());
        public Task<HashSet<EntityClassificationKey>> GetExistingEntityClassificationsAsync(IReadOnlyCollection<EntityClassificationKey> tuples, CancellationToken ct) => Task.FromResult(new HashSet<EntityClassificationKey>());
        public Task<HashSet<EdgeKey>> GetExistingEdgesAsync(IReadOnlyCollection<EdgeKey> tuples, CancellationToken ct) => Task.FromResult(new HashSet<EdgeKey>());
        public Task<HashSet<EdgeMemberKey>> GetExistingEdgeMembersAsync(IReadOnlyCollection<EdgeMemberKey> tuples, CancellationToken ct) => Task.FromResult(new HashSet<EdgeMemberKey>());
        public Task<HashSet<PhysicalityKey>> GetExistingPhysicalitiesAsync(IReadOnlyCollection<PhysicalityKey> tuples, CancellationToken ct) => Task.FromResult(new HashSet<PhysicalityKey>());
        public Task<HashSet<SequenceKey>> GetExistingSequenceRowsAsync(IReadOnlyCollection<SequenceKey> tuples, CancellationToken ct) => Task.FromResult(new HashSet<SequenceKey>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBatch : IIngestionBatch
    {
        public string ProvenanceCode => "test";
        public int EntityCount => 0;
        public int EdgeCount => 0;
        public EntityHandle AddEntity(Hash32 hash, string entityTypeCode) => new(hash, entityTypeCode);
        public void AddEdge(string edgeTypeCode, string provenanceCode, ReadOnlySpan<EdgeMemberSpec> members) { }
        public void AddJunction(string junctionTable, EntityHandle entity, int referenceId, double? mu = null, string attestationTypeCode = "lexical_curated_relation") { }
        public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geomWkb) { }
        public void AddPhysicalityPoint4d(EntityHandle entity, string physicalityTypeCode, double x1, double x2, double x3, double x4) { }
        public void AddPhysicalityLineString4d(EntityHandle entity, string physicalityTypeCode, ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices) { }
        public void AddSequence(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1) { }
        public void AddSignificance(EntityHandle entity, string contextTypeCode, double initialMu, string attestationTypeCode = "provenance_authority_corroboration") { }
        public void AddEntityModelSource(EntityHandle entity, long modelSourceId) { }
    }

    private sealed class FakeReporter : IProgressReporter
    {
        public Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
    }
}
