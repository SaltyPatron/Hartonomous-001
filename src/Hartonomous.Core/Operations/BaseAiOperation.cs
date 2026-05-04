using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Core.Operations;

public abstract partial class BaseAiOperation : IAiOperation
{
    protected NpgsqlDataSource DataSource { get; }

    protected ILogger Logger { get; }

    protected BaseAiOperation(NpgsqlDataSource dataSource, ILogger<BaseAiOperation> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        DataSource = dataSource;
        Logger = logger;
    }

    public abstract OperationCode Code { get; }

    public abstract ModalityLobe[] InputLobes { get; }

    public abstract ModalityLobe[] OutputLobes { get; }

    public async Task<OperationResponse> ExecuteAsync(OperationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        Stopwatch sw = Stopwatch.StartNew();
        OperationResponse core = await ExecuteCoreAsync(request, ct).ConfigureAwait(false);
        sw.Stop();

        OperationResponse result = core with { Elapsed = sw.Elapsed };

        Log.OperationCompleted(Logger, Code.Value, sw.Elapsed.TotalMilliseconds);

        return result;
    }

    protected abstract Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct);

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "operation completed {OperationCode} {ElapsedMs:F2}ms")]
        public static partial void OperationCompleted(ILogger logger, string operationCode, double elapsedMs);
    }
}
