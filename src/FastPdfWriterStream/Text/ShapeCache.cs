namespace FastPdfWriterStream.Text;

/// <summary>
/// کش متن‌های شکل‌دهی‌شده با کلید ReadOnlySpan&lt;char&gt; (بدون ساختن string برای جستجو).
/// برای متن‌های پرتکرار گزارش (شرح تراکنش، نام شعبه و ...) شکل‌دهی فقط یک بار انجام می‌شود.
/// </summary>
internal sealed class ShapeCache
{
    private struct Entry
    {
        public string Key;
        public int Hash;
        public byte Flags;
        public int Next;
        public ShapedText Value;
    }

    private int[] _buckets;
    private Entry[] _entries;
    private int _count;
    private readonly int _max;

    public ShapeCache(int max = 50_000)
    {
        _max = max;
        _buckets = new int[1024];
        _entries = new Entry[1024];
    }

    public int Count => _count;

    public void Clear()
    {
        Array.Clear(_buckets);
        Array.Clear(_entries, 0, _count);
        _count = 0;
    }

    private static int HashOf(ReadOnlySpan<char> key, byte flags) => string.GetHashCode(key) ^ (flags * 16777619);

    public bool TryGet(ReadOnlySpan<char> key, byte flags, out ShapedText value)
    {
        int h = HashOf(key, flags);
        int i = _buckets[(h & 0x7FFFFFFF) % _buckets.Length] - 1;
        while (i >= 0)
        {
            ref var e = ref _entries[i];
            if (e.Hash == h && e.Flags == flags && key.SequenceEqual(e.Key))
            {
                value = e.Value;
                return true;
            }
            i = e.Next;
        }
        value = null!;
        return false;
    }

    public void Add(ReadOnlySpan<char> key, byte flags, ShapedText value)
    {
        if (_count >= _max) Clear();
        if (_count == _entries.Length) Resize();
        int h = HashOf(key, flags);
        int b = (h & 0x7FFFFFFF) % _buckets.Length;
        _entries[_count] = new Entry { Key = key.ToString(), Hash = h, Flags = flags, Value = value, Next = _buckets[b] - 1 };
        _buckets[b] = _count + 1;
        _count++;
    }

    private void Resize()
    {
        int size = _entries.Length * 2;
        Array.Resize(ref _entries, size);
        _buckets = new int[size];
        for (int i = 0; i < _count; i++)
        {
            int b = (_entries[i].Hash & 0x7FFFFFFF) % size;
            _entries[i].Next = _buckets[b] - 1;
            _buckets[b] = i + 1;
        }
    }
}
