using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Engine.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hartonomous.Engine.Tests.Orchestration;

public sealed class SequentialPhaseRunnerTests
{
    private static SequentialPhaseRunner CreateRunner(
        Dictionary<Phase, IReadOnlyList<IDecomposer>>? decomposers = null)
    {
        return new SequentialPhaseRunner(
            decomposers ?? new Dictionary<Phase, IReadOnlyList<IDecomposer>>(),
            new FakePipeline(),
            new FakeReporter(),
            NullLogger<SequentialPhaseRunner>.Instance);
    }

    [Fact]
    public async Task RunPhase_NoDecomposers_Succeeds()
    {
        SequentialPhaseRunner runner = CreateRunner();

        PhaseResult result = await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);

        Assert.Equal(PhaseStatus.Completed, result.Status);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task RunPhase_WithDecomposer_CallsDecompose()
    {
        FakeDecomposer decomposer = new();
        Dictionary<Phase, IReadOnlyList<IDecomposer>> map = new()
        {
            [Phase.CoreAlgebra] = [decomposer],
        };
        SequentialPhaseRunner runner = CreateRunner(map);

        PhaseResult result = await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);

        Assert.Equal(PhaseStatus.Completed, result.Status);
        Assert.True(decomposer.DecomposeCalled);
    }

    [Fact]
    public async Task RunPhase_UnmetDependency_Fails()
    {
        SequentialPhaseRunner runner = CreateRunner();

        // UcdUca depends on CoreAlgebra which hasn't been run.
        PhaseResult result = await runner.RunPhaseAsync(Phase.UcdUca, CancellationToken.None);

        Assert.Equal(PhaseStatus.Failed, result.Status);
        Assert.Contains("CoreAlgebra", result.ErrorMessage);
    }

    [Fact]
    public async Task RunPhase_MetDependency_Succeeds()
    {
        SequentialPhaseRunner runner = CreateRunner();

        await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);
        PhaseResult result = await runner.RunPhaseAsync(Phase.UcdUca, CancellationToken.None);

        Assert.Equal(PhaseStatus.Completed, result.Status);
    }

    [Fact]
    public async Task RunPhase_DecomposerThrows_ReturnsFailed()
    {
        FakeDecomposer decomposer = new() { ShouldThrow = true };
        Dictionary<Phase, IReadOnlyList<IDecomposer>> map = new()
        {
            [Phase.CoreAlgebra] = [decomposer],
        };
        SequentialPhaseRunner runner = CreateRunner(map);

        PhaseResult result = await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);

        Assert.Equal(PhaseStatus.Failed, result.Status);
        Assert.Contains("decomposer failure", result.ErrorMessage);
    }

    [Fact]
    public async Task RunAll_ExecutesInTopologicalOrder()
    {
        List<Phase> executionOrder = [];
        Dictionary<Phase, IReadOnlyList<IDecomposer>> map = new();

        foreach (Phase phase in PhaseDag.TopologicalOrder())
        {
            FakeDecomposer d = new() { OnDecompose = () => executionOrder.Add(phase) };
            map[phase] = [d];
        }

        SequentialPhaseRunner runner = new(
            map, new FakePipeline(), new FakeReporter(),
            NullLogger<SequentialPhaseRunner>.Instance);

        IReadOnlyList<PhaseResult> results = await runner.RunAllAsync(CancellationToken.None);

        Assert.All(results, r => Assert.Equal(PhaseStatus.Completed, r.Status));

        IReadOnlyList<Phase> expectedOrder = PhaseDag.TopologicalOrder();
        Assert.Equal(expectedOrder, executionOrder);
    }

    [Fact]
    public async Task RunAll_HaltsOnFailure_SkipsRemaining()
    {
        FakeDecomposer failing = new() { ShouldThrow = true };
        Dictionary<Phase, IReadOnlyList<IDecomposer>> map = new()
        {
            [Phase.CoreAlgebra] = [failing],
        };
        SequentialPhaseRunner runner = CreateRunner(map);

        IReadOnlyList<PhaseResult> results = await runner.RunAllAsync(CancellationToken.None);

        PhaseResult first = results.First(r => r.Phase == Phase.CoreAlgebra);
        Assert.Equal(PhaseStatus.Failed, first.Status);

        // All subsequent phases should be NotStarted.
        foreach (PhaseResult r in results.Where(r => r.Phase != Phase.CoreAlgebra))
        {
            Assert.Equal(PhaseStatus.NotStarted, r.Status);
            Assert.Contains("Skipped", r.ErrorMessage);
        }
    }

    [Fact]
    public async Task RunAll_ReturnsResultsForAllPhases()
    {
        SequentialPhaseRunner runner = CreateRunner();

        IReadOnlyList<PhaseResult> results = await runner.RunAllAsync(CancellationToken.None);

        Assert.Equal(Enum.GetValues<Phase>().Length, results.Count);
    }

    [Fact]
    public async Task GetStatus_InitiallyAllNotStarted()
    {
        SequentialPhaseRunner runner = CreateRunner();

        IReadOnlyDictionary<Phase, PhaseStatus> status =
            await runner.GetStatusAsync(CancellationToken.None);

        Assert.All(status.Values, s => Assert.Equal(PhaseStatus.NotStarted, s));
    }

    [Fact]
    public async Task GetStatus_AfterRun_ReflectsCompletion()
    {
        SequentialPhaseRunner runner = CreateRunner();
        await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);

        IReadOnlyDictionary<Phase, PhaseStatus> status =
            await runner.GetStatusAsync(CancellationToken.None);

        Assert.Equal(PhaseStatus.Completed, status[Phase.CoreAlgebra]);
        Assert.Equal(PhaseStatus.NotStarted, status[Phase.UcdUca]);
    }

    [Fact]
    public async Task RunPhase_MultipleDecomposers_AllCalled()
    {
        FakeDecomposer d1 = new();
        FakeDecomposer d2 = new();
        Dictionary<Phase, IReadOnlyList<IDecomposer>> map = new()
        {
            [Phase.CoreAlgebra] = [d1, d2],
        };
        SequentialPhaseRunner runner = CreateRunner(map);

        PhaseResult result = await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);

        Assert.Equal(PhaseStatus.Completed, result.Status);
        Assert.True(d1.DecomposeCalled);
        Assert.True(d2.DecomposeCalled);
    }

    [Fact]
    public async Task RunPhase_Cancellation_PropagatesThrough()
    {
        using CancellationTokenSource cts = new();
        FakeDecomposer decomposer = new()
        {
            OnDecompose = () => cts.Cancel(),
            ShouldCheckCancellation = true,
        };
        Dictionary<Phase, IReadOnlyList<IDecomposer>> map = new()
        {
            [Phase.CoreAlgebra] = [decomposer],
        };
        SequentialPhaseRunner runner = CreateRunner(map);

        PhaseResult result = await runner.RunPhaseAsync(Phase.CoreAlgebra, cts.Token);

        Assert.Equal(PhaseStatus.Failed, result.Status);
    }

    [Fact]
    public async Task RunPhase_DeepDependencyChain_Validated()
    {
        // CoreAlgebra → UcdUca → Iso639 → WordNetOmw
        SequentialPhaseRunner runner = CreateRunner();

        // Can't skip to WordNetOmw.
        PhaseResult result = await runner.RunPhaseAsync(Phase.WordNetOmw, CancellationToken.None);
        Assert.Equal(PhaseStatus.Failed, result.Status);

        // Run the chain.
        await runner.RunPhaseAsync(Phase.CoreAlgebra, CancellationToken.None);
        result = await runner.RunPhaseAsync(Phase.WordNetOmw, CancellationToken.None);
        Assert.Equal(PhaseStatus.Failed, result.Status); // UcdUca not done

        await runner.RunPhaseAsync(Phase.UcdUca, CancellationToken.None);
        result = await runner.RunPhaseAsync(Phase.WordNetOmw, CancellationToken.None);
        Assert.Equal(PhaseStatus.Failed, result.Status); // Iso639 not done

        await runner.RunPhaseAsync(Phase.Iso639, CancellationToken.None);
        result = await runner.RunPhaseAsync(Phase.WordNetOmw, CancellationToken.None);
        Assert.Equal(PhaseStatus.Completed, result.Status); // All deps met
    }

    // ── Fakes ──

    private sealed class FakeDecomposer : IDecomposer
    {
        public string ProvenanceCode => "test";
        public string DisplayName => "Test Decomposer";
        public IReadOnlyList<Phase> Phases => [Phase.CoreAlgebra];
        public bool DecomposeCalled { get; private set; }
        public bool ShouldThrow { get; init; }
        public bool ShouldCheckCancellation { get; init; }
        public Action? OnDecompose { get; init; }

        public Task ValidateSourceAsync(CancellationToken ct) => Task.CompletedTask;

        public Task DecomposeAsync(IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
        {
            OnDecompose?.Invoke();
            if (ShouldCheckCancellation)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Simulated decomposer failure");
            }
            DecomposeCalled = true;
            return Task.CompletedTask;
        }

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
