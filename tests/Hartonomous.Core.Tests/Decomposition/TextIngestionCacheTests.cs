using System;
using Hartonomous.Core.Decomposition;
using Xunit;

namespace Hartonomous.Core.Tests.Decomposition;

public sealed class TextIngestionCacheTests
{
    private static byte[] H(byte b) => new byte[] { b };

    [Fact]
    public void Miss_then_hit_records_correct_counters()
    {
        TextIngestionCache c = new(capacity: 16);

        Assert.False(c.TryGet("foo", out _));
        c.Add("foo", H(1));
        Assert.True(c.TryGet("foo", out byte[]? hash));
        Assert.Equal(H(1), hash);

        Assert.Equal(1, c.Hits);
        Assert.Equal(1, c.Misses);
        Assert.Equal(0, c.Evictions);
        Assert.Equal(1, c.Count);
        Assert.Equal(0.5, c.HitRatio);
    }

    [Fact]
    public void Eviction_drops_least_recently_used_when_at_capacity()
    {
        TextIngestionCache c = new(capacity: 2);

        c.Add("a", H(1));
        c.Add("b", H(2));
        // Touch "a" so "b" becomes LRU.
        Assert.True(c.TryGet("a", out _));
        c.Add("c", H(3));

        Assert.True(c.TryGet("a", out _));
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("c", out _));
        Assert.Equal(1, c.Evictions);
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void Add_for_existing_key_is_noop()
    {
        TextIngestionCache c = new(capacity: 4);

        c.Add("k", H(1));
        c.Add("k", H(2));

        Assert.True(c.TryGet("k", out byte[]? hash));
        // First write wins; LRU semantics treat a duplicate Add as a no-op so
        // the cache does not evict-and-reinsert on repeated cache misses
        // racing the same content.
        Assert.Equal(H(1), hash);
        Assert.Equal(1, c.Count);
    }

    [Fact]
    public void Strings_longer_than_max_key_length_bypass_cache()
    {
        TextIngestionCache c = new(capacity: 4, maxKeyLength: 8);
        string huge = new('x', 16);

        c.Add(huge, H(1));
        Assert.False(c.TryGet(huge, out _));
        Assert.Equal(0, c.Count);
        Assert.Equal(1, c.SkippedTooLong);
    }

    [Fact]
    public void Hit_promotes_entry_to_most_recently_used()
    {
        TextIngestionCache c = new(capacity: 3);
        c.Add("a", H(1));
        c.Add("b", H(2));
        c.Add("c", H(3));
        // a is currently LRU. Touch a; now b is LRU.
        Assert.True(c.TryGet("a", out _));
        c.Add("d", H(4));

        Assert.True(c.TryGet("a", out _));
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("c", out _));
        Assert.True(c.TryGet("d", out _));
    }

    [Fact]
    public void Hit_ratio_is_zero_before_any_lookup()
    {
        TextIngestionCache c = new();
        Assert.Equal(0.0, c.HitRatio);
    }

    [Fact]
    public void Constructor_rejects_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextIngestionCache(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextIngestionCache(capacity: -1));
    }

    [Fact]
    public void Constructor_rejects_non_positive_max_key_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextIngestionCache(maxKeyLength: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextIngestionCache(maxKeyLength: -1));
    }
}
