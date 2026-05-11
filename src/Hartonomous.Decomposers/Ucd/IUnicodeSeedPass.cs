using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Decomposers.Ucd;

internal interface IUnicodeSeedPass
{
    string PassId { get; }

    IReadOnlyList<string> Dependencies { get; }

    Task RunAsync(UnicodePassContext context, CancellationToken ct);
}
