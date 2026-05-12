using Hartonomous.Core.Data;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

internal sealed partial class ExtensionCatalogVerificationPass : IUnicodeSeedPass
{
    public string PassId => "unicode.extension_catalog";

    public IReadOnlyList<string> Dependencies => [];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string version = await UnicodeSql.ExecuteScalarStringAsync(
            context.Connection,
            SubstrateFunctionNames.UcdVersion,
            ct);
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("substrate.ucd_version() returned empty; hartonomous extension UCD catalog is not available.");
        }

        Log.ExtensionVersion(context.Logger, version);
        await context.ReportAsync(PassId, 0, 0, ct);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "UCD/UCA extension catalog version {Version}")]
        public static partial void ExtensionVersion(ILogger logger, string version);
    }
}
