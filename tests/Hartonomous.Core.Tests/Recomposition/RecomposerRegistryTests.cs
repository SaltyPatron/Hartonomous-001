using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Hartonomous.Core.Analysis;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Core.Tests.Recomposition;

public sealed class RecomposerRegistryTests
{
    [Fact]
    public void Register_AndGet_ReturnsSameInstance_ForTextStringPair()
    {
        RecomposerRegistry registry = new();
        FakeStringRecomposer recomposer = new(Modality.Text);

        registry.Register<string>(recomposer);

        IRecomposer<string> resolved = registry.Resolve<string>(Modality.Text);

        Assert.Same(recomposer, resolved);
    }

    [Fact]
    public void TryGet_ReturnsTrue_WhenRegistered()
    {
        RecomposerRegistry registry = new();
        FakeStringRecomposer recomposer = new(Modality.Text);
        registry.Register<string>(recomposer);

        bool found = registry.TryResolve(Modality.Text, out IRecomposer<string>? resolved);

        Assert.True(found);
        Assert.Same(recomposer, resolved);
    }

    [Fact]
    public void TryGet_ReturnsFalse_AndNullOut_WhenMissing()
    {
        RecomposerRegistry registry = new();

        bool found = registry.TryResolve(Modality.Audio, out IRecomposer<string>? resolved);

        Assert.False(found);
        Assert.Null(resolved);
    }

    [Fact]
    public void Get_Throws_WithModalityListInMessage_WhenMissing()
    {
        RecomposerRegistry registry = new();
        registry.Register<string>(new FakeStringRecomposer(Modality.Text));

        KeyNotFoundException ex = Assert.Throws<KeyNotFoundException>(
            () => registry.Resolve<string>(Modality.Audio));

        Assert.Contains("Audio", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_DistinctTargets_SameModality_Coexist()
    {
        RecomposerRegistry registry = new();
        FakeStringRecomposer asString = new(Modality.Text);
        FakeBytesRecomposer asBytes = new(Modality.Text);

        registry.Register<string>(asString);
        registry.Register<byte[]>(asBytes);

        Assert.Same(asString, registry.Resolve<string>(Modality.Text));
        Assert.Same(asBytes, registry.Resolve<byte[]>(Modality.Text));
    }

    [Fact]
    public void Register_DuplicateKey_Throws()
    {
        RecomposerRegistry registry = new();
        registry.Register<string>(new FakeStringRecomposer(Modality.Text));

        Assert.Throws<InvalidOperationException>(
            () => registry.Register<string>(new FakeStringRecomposer(Modality.Text)));
    }

    [Fact]
    public void RegisteredModalities_ReturnsDistinctSet()
    {
        RecomposerRegistry registry = new();
        registry.Register<string>(new FakeStringRecomposer(Modality.Text));
        registry.Register<byte[]>(new FakeBytesRecomposer(Modality.Text));
        registry.Register<byte[]>(new FakeBytesRecomposer(Modality.Audio));

        Assert.Equal(2, registry.RegisteredModalities.Count);
        Assert.Contains(Modality.Text, registry.RegisteredModalities);
        Assert.Contains(Modality.Audio, registry.RegisteredModalities);
    }

    [Fact]
    public void Register_UnsupportedTarget_Throws()
    {
        RecomposerRegistry registry = new();

        Assert.Throws<NotSupportedException>(
            () => registry.Register<int>(new FakeIntRecomposer(Modality.Text)));
    }

    private sealed class FakeStringRecomposer : IRecomposer<string>
    {
        public FakeStringRecomposer(Modality modality) => OutputModality = modality;

        public Modality OutputModality { get; }

        public Task<string> RecomposeAsync(EntityHandle entity, RecompositionOptions options, CancellationToken ct)
            => Task.FromResult(string.Empty);

        public Task RecomposeToStreamAsync(EntityHandle entity, RecompositionOptions options, Stream output, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeBytesRecomposer : IRecomposer<byte[]>
    {
        public FakeBytesRecomposer(Modality modality) => OutputModality = modality;

        public Modality OutputModality { get; }

        public Task<byte[]> RecomposeAsync(EntityHandle entity, RecompositionOptions options, CancellationToken ct)
            => Task.FromResult(Array.Empty<byte>());

        public Task RecomposeToStreamAsync(EntityHandle entity, RecompositionOptions options, Stream output, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeIntRecomposer : IRecomposer<int>
    {
        public FakeIntRecomposer(Modality modality) => OutputModality = modality;

        public Modality OutputModality { get; }

        public Task<int> RecomposeAsync(EntityHandle entity, RecompositionOptions options, CancellationToken ct)
            => Task.FromResult(0);

        public Task RecomposeToStreamAsync(EntityHandle entity, RecompositionOptions options, Stream output, CancellationToken ct)
            => Task.CompletedTask;
    }
}
