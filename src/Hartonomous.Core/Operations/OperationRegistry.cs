using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Hartonomous.Core.Operations;

public sealed class OperationRegistry : IOperationRegistry
{
    private readonly Dictionary<OperationCode, IAiOperation> _ops;

    public OperationRegistry(IEnumerable<IAiOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        _ops = new Dictionary<OperationCode, IAiOperation>();
        foreach (IAiOperation op in operations)
        {
            if (_ops.TryGetValue(op.Code, out IAiOperation? existing))
            {
                throw new InvalidOperationException(
                    $"Duplicate operation code '{op.Code.Value}' registered by both " +
                    $"'{existing.GetType().FullName}' and '{op.GetType().FullName}'.");
            }
            _ops.Add(op.Code, op);
        }
    }

    public IAiOperation Resolve(OperationCode code)
    {
        if (_ops.TryGetValue(code, out IAiOperation? op))
        {
            return op;
        }

        string registered = string.Join(
            ", ",
            _ops.Keys.Select(k => k.Value).OrderBy(v => v, StringComparer.Ordinal));
        throw new KeyNotFoundException(
            $"No operation registered for code '{code.Value}'. Registered codes: [{registered}].");
    }

    public bool TryResolve(OperationCode code, [NotNullWhen(true)] out IAiOperation? op)
    {
        return _ops.TryGetValue(code, out op);
    }

    public IReadOnlyCollection<OperationCode> RegisteredCodes => _ops.Keys;
}
