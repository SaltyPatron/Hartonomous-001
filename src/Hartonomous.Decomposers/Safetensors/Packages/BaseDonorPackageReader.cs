using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Packages;

public abstract class BaseDonorPackageReader : IDonorPackageReader
{
    private static readonly string[] WellKnownArtifactNames =
    {
        "tokenizer.json",
        "tokenizer.model",
        "tokenizer_config.json",
        "special_tokens_map.json",
        "vocab.json",
        "merges.txt",
        "sentencepiece.model",
        "generation_config.json",
        "preprocessor_config.json",
        "processor_config.json",
    };

    protected string PackageRootInternal { get; }
    protected ILogger Logger { get; }

    protected BaseDonorPackageReader(string packageRoot, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            throw new ArgumentException("Package root must not be null or whitespace.", nameof(packageRoot));
        }
        ArgumentNullException.ThrowIfNull(logger);

        PackageRootInternal = packageRoot;
        Logger = logger;
    }

    public string PackageRoot => PackageRootInternal;

    public abstract string PackageFormat { get; }

    public virtual IReadOnlyList<string> AdditionalArtifacts
    {
        get
        {
            List<string> found = new(WellKnownArtifactNames.Length);
            foreach (string name in WellKnownArtifactNames)
            {
                string absolute = Path.Combine(PackageRootInternal, name);
                if (File.Exists(absolute))
                {
                    found.Add(name);
                }
            }
            return found;
        }
    }

    public abstract IReadOnlyList<TensorMetadata> EnumerateTensors();

    public abstract Task<ReadOnlyMemory<byte>> ReadTensorAsync(string name, CancellationToken ct);

    public abstract IConfigSnapshot ReadConfig();

    protected static async Task<JsonDocument> LoadJsonAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
