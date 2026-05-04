using System.Collections.Concurrent;

namespace Hartonomous.Decomposers.Safetensors.Packages;

/// <summary>
/// Process-wide registry that lets SafetensorsTensorInfo records produced by
/// non-safetensors readers (PyTorchPicklePackageReader, MultiSubdirPackageReader)
/// route their tensor-byte reads through the owning IDonorPackageReader.
///
/// The convention: a tensor whose FilePath starts with the prefix returned by
/// <see cref="BuildPath"/> is donor-routed. SafetensorsReader's internal
/// stream-open helper detects the prefix, looks up the slot, and reads via
/// the registered IDonorPackageReader.
///
/// Slots are allocated at decomposer-discovery time and freed when the
/// decomposer disposes the donor session. The registry is intentionally
/// process-wide rather than per-session so static helpers (the existing
/// SafetensorsReader.StreamHash etc.) can resolve readers without plumbing
/// new context through every call site.
/// </summary>
public static class DonorReaderRegistry
{
    public const string Scheme = "donor://";

    private static readonly ConcurrentDictionary<int, IDonorPackageReader> _readers = new();
    private static int _nextSlot;

    public static int Register(IDonorPackageReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int slot = Interlocked.Increment(ref _nextSlot);
        _readers[slot] = reader;
        return slot;
    }

    public static void Release(int slot)
    {
        _readers.TryRemove(slot, out _);
    }

    public static IDonorPackageReader Resolve(int slot)
    {
        if (!_readers.TryGetValue(slot, out IDonorPackageReader? r))
        {
            throw new KeyNotFoundException($"DonorReaderRegistry: slot {slot} not registered (released or never created).");
        }
        return r;
    }

    public static string BuildPath(int slot, string tensorName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tensorName);
        return $"{Scheme}{slot}/{Uri.EscapeDataString(tensorName)}";
    }

    public static bool TryParsePath(string filePath, out int slot, out string tensorName)
    {
        slot = 0;
        tensorName = string.Empty;
        if (string.IsNullOrEmpty(filePath) || !filePath.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }
        string rest = filePath[Scheme.Length..];
        int sep = rest.IndexOf('/');
        if (sep <= 0 || sep == rest.Length - 1)
        {
            return false;
        }
        if (!int.TryParse(rest[..sep], System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out slot))
        {
            return false;
        }
        tensorName = Uri.UnescapeDataString(rest[(sep + 1)..]);
        return true;
    }

    public static bool IsRegistered(int slot) => _readers.ContainsKey(slot);
}
