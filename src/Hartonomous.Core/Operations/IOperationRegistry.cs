using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Hartonomous.Core.Operations;

public interface IOperationRegistry
{
    IAiOperation Resolve(OperationCode code);

    bool TryResolve(OperationCode code, [NotNullWhen(true)] out IAiOperation? op);

    IReadOnlyCollection<OperationCode> RegisteredCodes { get; }
}
