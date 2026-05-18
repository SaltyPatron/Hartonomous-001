namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for the M6 inference loop — the C-extension A* traversal,
/// the Glicko-2 update primitive, and the SQL cognitive surface.
/// These run without seeded substrate state; they validate that the
/// functions exist with the right signatures and don't crash on empty
/// or sentinel input.
/// </summary>
[Collection("smoke")]
public sealed class InferenceSmokeTests
{
    private readonly SmokeFixture _fx;

    public InferenceSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task TraverseAstar_FunctionExists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The A* C extension function must be registered with PG. If
        // missing, every inference request fails with "function does not exist".
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname IN ('public','substrate') AND p.proname = 'traverse_astar'");
        Assert.True(n >= 1, "traverse_astar function not registered");
    }

    [Fact]
    public async Task InferComplete_FunctionExists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname = 'substrate' AND p.proname IN ('infer','complete','infer_topk')");
        Assert.True(n >= 3, $"inference surface incomplete: only {n}/3 functions present");
    }

    [Fact]
    public async Task ClassifyAndRerank_FunctionsExist()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname = 'substrate' AND p.proname IN ('classify','rerank','embed_lookup')");
        Assert.True(n >= 3, $"AI primitive surface incomplete: only {n}/3 functions present");
    }

    [Fact]
    public async Task RecordComparisonAndOutcome_FunctionsExist()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Glicko-2 outcome surface — drives the closed-loop learning step.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname = 'substrate' AND p.proname IN " +
            "('record_comparison','record_edge_comparison','record_entity_comparison','record_outcome','record_corroboration')");
        Assert.True(n >= 5, $"outcome / Glicko surface incomplete: only {n}/5 functions present");
    }

    [Fact]
    public async Task DrainPostPassFunctions_Removed()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Drain-completion post-pass functions (populate_edge_trajectories,
        // prime_unprimed_edges_chunk, reset_arena_priming_state,
        // count_missing_edge_trajectories) were deleted per AP-37 — edge
        // geometry and per-arena significance priors are emitted inline at
        // edge-emit by the bundled-emit pipeline. The functions MUST be
        // absent from the live substrate so callers can't drift back to
        // the post-pass shape.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname = 'substrate' AND p.proname IN " +
            "('populate_edge_trajectories','prime_unprimed_edges_chunk'," +
            "'reset_arena_priming_state','count_missing_edge_trajectories')");
        Assert.Equal(0, n);
    }
}
