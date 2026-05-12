namespace Hartonomous.Engine.Ingestion;

internal readonly record struct IngestionResourceSnapshot(
    double ProcessCpuPercent,
    double ProcessCpuCores,
    double SystemCpuPercent,
    double SystemIoWaitPercent,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    int ThreadPoolBusyWorkers,
    int ThreadPoolMaxWorkers,
    int ThreadPoolBusyIo,
    int ThreadPoolMaxIo,
    long SystemMemoryTotalBytes,
    long SystemMemoryAvailableBytes,
    long SwapTotalBytes,
    long SwapFreeBytes,
    double ProcessReadMibPerSec,
    double ProcessWriteMibPerSec,
    long RootDriveTotalBytes,
    long RootDriveAvailableBytes,
    long PostgresDriveTotalBytes,
    long PostgresDriveAvailableBytes);
