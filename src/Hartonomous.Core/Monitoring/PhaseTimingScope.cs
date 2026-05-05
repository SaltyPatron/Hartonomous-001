using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Core.Monitoring;

/// <summary>
/// Per-stage timing aggregator for decomposers. Wrap the body of each named
/// stage in a <c>using (timing.Step("parse")) { ... }</c> block; on disposal
/// the scope logs a one-line summary that breaks down where the phase's
/// wall-clock time actually went.
///
/// Without this, decomposers report only entity/edge counts and total elapsed
/// — which means "WordNet took 10 minutes" reveals nothing about whether the
/// 10 minutes were file parsing, per-synset Merkle hashing, the per-row
/// EmitText fan-out, or pipeline backpressure.
/// </summary>
public sealed partial class PhaseTimingScope : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _phaseLabel;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly List<(string Stage, TimeSpan Elapsed)> _stages = new();
    private bool _disposed;

    public PhaseTimingScope(ILogger logger, string phaseLabel)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _phaseLabel = phaseLabel ?? throw new ArgumentNullException(nameof(phaseLabel));
    }

    /// <summary>
    /// Begin timing a named stage. Dispose the returned scope (or wrap in
    /// <c>using</c>) to record the stage's elapsed time. Stages are NOT
    /// nested — wall-clock per stage is what's logged.
    /// </summary>
    public IDisposable Step(string stageName) => new StageScope(this, stageName);

    /// <summary>
    /// Add a stage entry directly without a <see cref="Step(string)"/> scope.
    /// Useful when the timing source is external (e.g. a Stopwatch handed
    /// across an async boundary).
    /// </summary>
    public void Record(string stageName, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(stageName);
        lock (_stages)
        {
            _stages.Add((stageName, elapsed));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _total.Stop();
        LogSummary();
    }

    /// <summary>
    /// Emit the breakdown line. Called automatically from <see cref="Dispose"/>;
    /// callable explicitly when the scope outlives a logical phase boundary.
    /// </summary>
    public void LogSummary()
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }
        StringBuilder sb = new();
        sb.Append(_phaseLabel).Append(" timing: ");
        TimeSpan accountedFor = TimeSpan.Zero;
        lock (_stages)
        {
            for (int i = 0; i < _stages.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                (string stage, TimeSpan elapsed) = _stages[i];
                sb.Append(stage).Append('=').Append(FormatElapsed(elapsed));
                accountedFor += elapsed;
            }
        }
        TimeSpan totalElapsed = _total.Elapsed;
        TimeSpan unaccounted = totalElapsed - accountedFor;
        sb.Append(" total=").Append(FormatElapsed(totalElapsed));
        if (unaccounted > TimeSpan.FromMilliseconds(50))
        {
            sb.Append(" untimed=").Append(FormatElapsed(unaccounted));
        }

#pragma warning disable CA1873 // IsEnabled check is at the top of LogSummary; analyzer doesn't see across StringBuilder construction.
        Log.PhaseTiming(_logger, sb.ToString());
#pragma warning restore CA1873
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalMilliseconds:F0}ms");
        }
        if (elapsed.TotalMinutes < 1.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:F1}s");
        }
        int totalSeconds = (int)elapsed.TotalSeconds;
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes}m{seconds:D2}s");
    }

    private sealed class StageScope : IDisposable
    {
        private readonly PhaseTimingScope _parent;
        private readonly string _stageName;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _done;

        public StageScope(PhaseTimingScope parent, string stageName)
        {
            _parent = parent;
            _stageName = stageName;
        }

        public void Dispose()
        {
            if (_done)
            {
                return;
            }
            _done = true;
            _sw.Stop();
            _parent.Record(_stageName, _sw.Elapsed);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 71011501, Level = LogLevel.Information,
            Message = "{Summary}")]
        public static partial void PhaseTiming(ILogger logger, string summary);
    }
}
