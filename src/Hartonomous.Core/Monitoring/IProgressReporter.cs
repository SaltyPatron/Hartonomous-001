using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Monitoring;

public interface IProgressReporter
{
    Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct);
}
