using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Operations;

public interface IAiOperation
{
    OperationCode Code { get; }

    ModalityLobe[] InputLobes { get; }

    ModalityLobe[] OutputLobes { get; }

    Task<OperationResponse> ExecuteAsync(OperationRequest request, CancellationToken ct);
}
