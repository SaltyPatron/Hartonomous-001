using System;
using System.Collections.Generic;

namespace Hartonomous.Core.Decomposition;

/// <summary>
/// Bounded LRU cache mapping a UTF-16 source string to its already-decomposed
/// text_composition document hash. Used by decomposers that ingest the same
/// text content many times in one run (Wiktionary etymology boilerplate,
/// gloss patterns, IPA strings, WordNet definition templates) to short-circuit
/// re-segmentation and re-emission of the codepoint → grapheme → word_form
/// → text_composition DAG. The substrate row is created on first occurrence;
/// subsequent occurrences register the document entity by hash on the current
/// batch and skip all sub-tree work.
///
/// Thread-safe via a single coarse lock. Decomposers that fan their producer
/// out across N tasks (ParallelChunkProcessor pattern) all share one cache,
/// so concurrent TryGet / Add are protected. Lookups are O(1) under the
/// dictionary; the lock holds for the LRU move-to-front + eviction step
/// only, which is microsecond-scale and not contention-bound for ingestion
/// workloads at typical fanout (4-16 parallel tasks).
///
/// Strings longer than <see cref="MaxKeyLength"/> are not cached: large
/// model artifacts (READMEs, config blobs) rarely repeat exactly across
/// entries and would otherwise dominate cache memory.
/// </summary>
public sealed class TextIngestionCache
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly int _maxKeyLength;
    private readonly LinkedList<KeyValuePair<string, byte[]>> _list = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, byte[]>>> _index;

    public TextIngestionCache(int capacity = 100_000, int maxKeyLength = 8192)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }
        if (maxKeyLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), "MaxKeyLength must be positive.");
        }
        _capacity = capacity;
        _maxKeyLength = maxKeyLength;
        _index = new Dictionary<string, LinkedListNode<KeyValuePair<string, byte[]>>>(
            capacity, StringComparer.Ordinal);
    }

    public int Capacity => _capacity;
    public int MaxKeyLength => _maxKeyLength;
    public int Count => _index.Count;

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long SkippedTooLong { get; private set; }

    /// <summary>
    /// True hit ratio over (Hits + Misses), expressed in [0, 1]. Returns 0 if
    /// no lookups have occurred. Strings rejected by the length cap are not
    /// counted as misses (they bypass the cache entirely).
    /// </summary>
    public double HitRatio
    {
        get
        {
            long total = Hits + Misses;
            return total == 0 ? 0.0 : (double)Hits / total;
        }
    }

    public bool TryGet(string text, out byte[]? hash)
    {
        if (text.Length > _maxKeyLength)
        {
            hash = null;
            return false;
        }
        lock (_gate)
        {
            if (_index.TryGetValue(text, out LinkedListNode<KeyValuePair<string, byte[]>>? node))
            {
                // Move-to-front: the just-touched node is now most recently used.
                _list.Remove(node);
                _list.AddFirst(node);
                hash = node.Value.Value;
                Hits++;
                return true;
            }
            hash = null;
            Misses++;
            return false;
        }
    }

    public void Add(string text, byte[] hash)
    {
        if (text.Length > _maxKeyLength)
        {
            lock (_gate)
            {
                SkippedTooLong++;
            }
            return;
        }
        lock (_gate)
        {
            if (_index.ContainsKey(text))
            {
                // Already cached — TryGet would have hit (or another thread
                // added concurrently). No-op to keep LRU semantics simple.
                return;
            }
            if (_index.Count >= _capacity)
            {
                // Evict least-recently-used (tail of list).
                LinkedListNode<KeyValuePair<string, byte[]>>? lru = _list.Last;
                if (lru is not null)
                {
                    _list.RemoveLast();
                    _index.Remove(lru.Value.Key);
                    Evictions++;
                }
            }
            LinkedListNode<KeyValuePair<string, byte[]>> node = new(new KeyValuePair<string, byte[]>(text, hash));
            _list.AddFirst(node);
            _index[text] = node;
        }
    }
}
