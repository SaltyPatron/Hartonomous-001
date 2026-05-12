using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Hartonomous.Engine.Ingestion;

internal sealed class IngestionResourceMonitor
{
    private const double BytesPerMib = 1024.0 * 1024.0;

    private readonly Process _process = Process.GetCurrentProcess();
    private long _lastTimestamp;
    private TimeSpan _lastProcessorTime;
    private long _lastReadBytes;
    private long _lastWriteBytes;
    private CpuCounters _lastCpuCounters;

    public IngestionResourceSnapshot Capture()
    {
        long now = Stopwatch.GetTimestamp();
        _process.Refresh();

        TimeSpan processorTime = _process.TotalProcessorTime;
        double elapsedSeconds = _lastTimestamp == 0
            ? 0.0
            : (double)(now - _lastTimestamp) / Stopwatch.Frequency;
        double processCpuCores = elapsedSeconds > 0.0
            ? Math.Max(0.0, (processorTime - _lastProcessorTime).TotalSeconds / elapsedSeconds)
            : 0.0;
        double processCpuPercent = Environment.ProcessorCount > 0
            ? 100.0 * processCpuCores / Environment.ProcessorCount
            : 0.0;

        _lastTimestamp = now;
        _lastProcessorTime = processorTime;

        ThreadPool.GetAvailableThreads(out int availableWorkers, out int availableIo);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        CpuCounters cpuCounters = ReadCpuCounters();
        (double systemCpuPercent, double systemIoWaitPercent) = ComputeSystemCpu(cpuCounters);
        ReadMemInfo(out long totalMemory, out long availableMemory, out long totalSwap, out long freeSwap);
        ReadProcIo(out long readBytes, out long writeBytes);

        double readMibPerSec = _lastReadBytes == 0 || elapsedSeconds <= 0.0
            ? 0.0
            : Math.Max(0.0, readBytes - _lastReadBytes) / BytesPerMib / elapsedSeconds;
        double writeMibPerSec = _lastWriteBytes == 0 || elapsedSeconds <= 0.0
            ? 0.0
            : Math.Max(0.0, writeBytes - _lastWriteBytes) / BytesPerMib / elapsedSeconds;
        _lastReadBytes = readBytes;
        _lastWriteBytes = writeBytes;

        (long rootTotal, long rootAvailable) = DriveStats("/");
        (long pgTotal, long pgAvailable) = DriveStats("/var/lib/postgresql");

        return new IngestionResourceSnapshot(
            ProcessCpuPercent: processCpuPercent,
            ProcessCpuCores: processCpuCores,
            SystemCpuPercent: systemCpuPercent,
            SystemIoWaitPercent: systemIoWaitPercent,
            WorkingSetBytes: _process.WorkingSet64,
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            ThreadPoolBusyWorkers: maxWorkers - availableWorkers,
            ThreadPoolMaxWorkers: maxWorkers,
            ThreadPoolBusyIo: maxIo - availableIo,
            ThreadPoolMaxIo: maxIo,
            SystemMemoryTotalBytes: totalMemory,
            SystemMemoryAvailableBytes: availableMemory,
            SwapTotalBytes: totalSwap,
            SwapFreeBytes: freeSwap,
            ProcessReadMibPerSec: readMibPerSec,
            ProcessWriteMibPerSec: writeMibPerSec,
            RootDriveTotalBytes: rootTotal,
            RootDriveAvailableBytes: rootAvailable,
            PostgresDriveTotalBytes: pgTotal,
            PostgresDriveAvailableBytes: pgAvailable);
    }

    private (double CpuPercent, double IoWaitPercent) ComputeSystemCpu(CpuCounters current)
    {
        if (_lastCpuCounters.Total == 0)
        {
            _lastCpuCounters = current;
            return (0.0, 0.0);
        }

        long totalDelta = current.Total - _lastCpuCounters.Total;
        long idleDelta = current.IdleAll - _lastCpuCounters.IdleAll;
        long ioWaitDelta = current.IoWait - _lastCpuCounters.IoWait;
        _lastCpuCounters = current;
        if (totalDelta <= 0)
        {
            return (0.0, 0.0);
        }

        double cpuPercent = 100.0 * Math.Max(0L, totalDelta - idleDelta) / totalDelta;
        double ioWaitPercent = 100.0 * Math.Max(0L, ioWaitDelta) / totalDelta;
        return (cpuPercent, ioWaitPercent);
    }

    private static CpuCounters ReadCpuCounters()
    {
        const string path = "/proc/stat";
        if (!File.Exists(path))
        {
            return default;
        }

        using StreamReader reader = File.OpenText(path);
        string? line = reader.ReadLine();
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return default;
        }

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long user = ParseCounter(parts, 1);
        long nice = ParseCounter(parts, 2);
        long system = ParseCounter(parts, 3);
        long idle = ParseCounter(parts, 4);
        long ioWait = ParseCounter(parts, 5);
        long irq = ParseCounter(parts, 6);
        long softIrq = ParseCounter(parts, 7);
        long steal = ParseCounter(parts, 8);
        long guest = ParseCounter(parts, 9);
        long guestNice = ParseCounter(parts, 10);
        long total = user + nice + system + idle + ioWait + irq + softIrq + steal + guest + guestNice;
        return new CpuCounters(total, idle + ioWait, ioWait);
    }

    private static long ParseCounter(string[] parts, int index)
        => parts.Length > index && long.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : 0L;

    private static void ReadMemInfo(out long total, out long available, out long swapTotal, out long swapFree)
    {
        total = 0;
        available = 0;
        swapTotal = 0;
        swapFree = 0;
        const string path = "/proc/meminfo";
        if (!File.Exists(path))
        {
            return;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = ParseMemInfoBytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = ParseMemInfoBytes(line);
            }
            else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal))
            {
                swapTotal = ParseMemInfoBytes(line);
            }
            else if (line.StartsWith("SwapFree:", StringComparison.Ordinal))
            {
                swapFree = ParseMemInfoBytes(line);
            }
        }
    }

    private static long ParseMemInfoBytes(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kib)
            ? kib * 1024L
            : 0L;
    }

    private static void ReadProcIo(out long readBytes, out long writeBytes)
    {
        readBytes = 0;
        writeBytes = 0;
        const string path = "/proc/self/io";
        if (!File.Exists(path))
        {
            return;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
            {
                readBytes = ParseProcIoBytes(line);
            }
            else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
            {
                writeBytes = ParseProcIoBytes(line);
            }
        }
    }

    private static long ParseProcIoBytes(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
            ? bytes
            : 0L;
    }

    private static (long Total, long Available) DriveStats(string path)
    {
        DriveInfo? best = null;
        string fullPath = Path.GetFullPath(path);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || !fullPath.StartsWith(drive.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (best is null || drive.Name.Length > best.Name.Length)
            {
                best = drive;
            }
        }

        return best is null
            ? (0L, 0L)
            : (best.TotalSize, best.AvailableFreeSpace);
    }

    private readonly record struct CpuCounters(long Total, long IdleAll, long IoWait);
}
