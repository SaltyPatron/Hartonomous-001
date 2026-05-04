using System;
using System.Collections.Generic;
using System.Linq;

using Hartonomous.Core.Analysis;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// In-memory registry mapping (Modality, TTarget) to the concrete recomposer
/// for that pair. Built by calling <see cref="Register{TTarget}"/> at the
/// composition root once at startup; not thread-safe — registration must
/// complete before any reader thread calls <see cref="Resolve{TTarget}"/>.
/// Two recomposers for the same modality with different output targets
/// (e.g. text-to-string and text-to-bytes) coexist under distinct keys.
/// V1 supports <see cref="string"/> and <see cref="T:byte[]"/> targets.
/// </summary>
public sealed class RecomposerRegistry : IRecomposerRegistry
{
    private readonly Dictionary<(Modality Modality, Type Target), object> _byKey = new();

    public IReadOnlyCollection<Modality> RegisteredModalities
        => _byKey.Keys.Select(k => k.Modality).Distinct().ToArray();

    public void Register<TTarget>(IRecomposer<TTarget> recomposer) where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(recomposer);
        EnsureSupportedTarget(typeof(TTarget));

        var key = (recomposer.OutputModality, typeof(TTarget));
        if (_byKey.ContainsKey(key))
        {
            throw new InvalidOperationException(
                $"A recomposer is already registered for modality '{key.OutputModality}' with target '{key.Item2.FullName}'.");
        }

        _byKey[key] = recomposer;
    }

    public IRecomposer<TTarget> Resolve<TTarget>(Modality modality) where TTarget : notnull
    {
        EnsureSupportedTarget(typeof(TTarget));

        if (_byKey.TryGetValue((modality, typeof(TTarget)), out object? entry))
        {
            return (IRecomposer<TTarget>)entry;
        }

        string registered = string.Join(
            ", ",
            _byKey.Keys.Select(k => $"({k.Modality}, {k.Target.Name})").OrderBy(s => s, StringComparer.Ordinal));
        if (registered.Length == 0)
        {
            registered = "<none>";
        }

        throw new KeyNotFoundException(
            $"No recomposer registered for modality '{modality}' with target '{typeof(TTarget).FullName}'. Registered: {registered}.");
    }

    public bool TryResolve<TTarget>(Modality modality, out IRecomposer<TTarget>? recomposer) where TTarget : notnull
    {
        EnsureSupportedTarget(typeof(TTarget));

        if (_byKey.TryGetValue((modality, typeof(TTarget)), out object? entry))
        {
            recomposer = (IRecomposer<TTarget>)entry;
            return true;
        }

        recomposer = null;
        return false;
    }

    private static void EnsureSupportedTarget(Type target)
    {
        if (target != typeof(string) && target != typeof(byte[]))
        {
            throw new NotSupportedException(
                $"RecomposerRegistry V1 supports targets 'System.String' and 'System.Byte[]'; got '{target.FullName}'.");
        }
    }
}
