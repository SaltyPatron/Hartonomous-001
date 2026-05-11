using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

internal sealed partial class UnicodePassOrchestrator
{
    private readonly IReadOnlyList<IUnicodeSeedPass> _passes;
    private readonly ILogger _logger;

    public UnicodePassOrchestrator(IReadOnlyList<IUnicodeSeedPass> passes, ILogger logger)
    {
        _passes = passes;
        _logger = logger;
    }

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        IReadOnlyList<IUnicodeSeedPass> ordered = OrderPasses(_passes);
        HashSet<string> completed = [];

        foreach (IUnicodeSeedPass pass in ordered)
        {
            ct.ThrowIfCancellationRequested();
            foreach (string dependency in pass.Dependencies)
            {
                if (!completed.Contains(dependency))
                {
                    throw new InvalidOperationException(
                        $"Unicode pass {pass.PassId} cannot run before dependency {dependency}.");
                }
            }

            Log.PassStart(_logger, pass.PassId);
            await pass.RunAsync(context, ct);
            completed.Add(pass.PassId);
            Log.PassComplete(_logger, pass.PassId);
        }
    }

    private static List<IUnicodeSeedPass> OrderPasses(IReadOnlyList<IUnicodeSeedPass> passes)
    {
        Dictionary<string, IUnicodeSeedPass> byId = passes.ToDictionary(p => p.PassId, StringComparer.Ordinal);
        List<IUnicodeSeedPass> ordered = [];
        HashSet<string> visiting = [];
        HashSet<string> visited = [];

        foreach (IUnicodeSeedPass pass in passes)
        {
            Visit(pass, byId, ordered, visiting, visited);
        }

        return ordered;
    }

    private static void Visit(
        IUnicodeSeedPass pass,
        IReadOnlyDictionary<string, IUnicodeSeedPass> byId,
        List<IUnicodeSeedPass> ordered,
        HashSet<string> visiting,
        HashSet<string> visited)
    {
        if (visited.Contains(pass.PassId))
        {
            return;
        }

        if (!visiting.Add(pass.PassId))
        {
            throw new InvalidOperationException($"Cycle in Unicode pass graph at {pass.PassId}.");
        }

        foreach (string dependency in pass.Dependencies)
        {
            if (!byId.TryGetValue(dependency, out IUnicodeSeedPass? dependencyPass))
            {
                throw new InvalidOperationException(
                    $"Unicode pass {pass.PassId} depends on missing pass {dependency}.");
            }

            Visit(dependencyPass, byId, ordered, visiting, visited);
        }

        visiting.Remove(pass.PassId);
        visited.Add(pass.PassId);
        ordered.Add(pass);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode pass {PassId} started")]
        public static partial void PassStart(ILogger logger, string passId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode pass {PassId} completed")]
        public static partial void PassComplete(ILogger logger, string passId);
    }
}
