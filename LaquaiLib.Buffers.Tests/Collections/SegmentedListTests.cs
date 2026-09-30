using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;

using LaquaiLib.Buffers.Collections;

namespace LaquaiLib.Buffers.Tests.Collections;

public class SegmentedListTests
{
    [StructLayout(LayoutKind.Sequential, Size = 8000)]
    private struct Huge
    {
        public byte B;
    }

    [StructLayout(LayoutKind.Sequential, Size = 50000)]
    private struct Enormous
    {
        public byte B;
    }

    private sealed class NullPool<T> : ArrayPool<T>
    {
        public override T[] Rent(int minimumLength) => null;
        public override void Return(T[] array, bool clearArray = false) { }
    }

    private static void SetField<T>(SegmentedListBase<T> list, string name, int value)
        => typeof(SegmentedListBase<T>).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(list, value);

    private sealed class CapturingPool<T>(int shortBy = 0) : ArrayPool<T>
    {
        public readonly List<T[]> Allocated = [];
        public readonly List<T[]> Released = [];

        public override T[] Rent(int minimumLength)
        {
            var array = new T[minimumLength - shortBy];
            Allocated.Add(array);
            return array;
        }
        public override void Return(T[] array, bool clearArray = false) => Released.Add(array);
        public int NonNullSlots() => Allocated.Except(Released).Sum(static a => a.Count(static x => x is not null));
    }

    private sealed class HugeCollection<T>(int count) : ICollection<T>
    {
        public int Count => count;
        public bool IsReadOnly => true;
        public void Add(T item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(T item) => throw new NotSupportedException();
        public void CopyTo(T[] array, int arrayIndex) => throw new NotSupportedException();
        public bool Remove(T item) => throw new NotSupportedException();
        public IEnumerator<T> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class HugeReadOnlyCollection<T>(int count) : IReadOnlyCollection<T>
    {
        public int Count => count;
        public IEnumerator<T> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ReadOnlyOnly<T>(IEnumerable<T> items) : IReadOnlyCollection<T>
    {
        private readonly List<T> _items = [.. items];
        public int Count => _items.Count;
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static TheoryData<int, int> Configs => new()
    {
        { 1, 1 },
        { 1, 8 },
        { 4, 4 },
        { 4, 64 },
        { 3, 13 },
        { 16, 1 << 30 },
    };

    public static TheoryData<int, int, int> RandomConfigs
    {
        get
        {
            var data = new TheoryData<int, int, int>();
            foreach (var (min, max) in new[] { (1, 1), (1, 16), (4, 4), (4, 64), (16, 1 << 30) })
            {
                for (var seed = 0; seed < 3; seed++)
                    data.Add(min, max, seed);
            }
            return data;
        }
    }

    private static IEnumerable<int> Yield(params int[] items)
    {
        foreach (var item in items)
            yield return item;
    }

    private static SegmentedList<int> Filled(int min, int max, int count)
    {
        var list = new SegmentedList<int>(min, max);
        for (var i = 0; i < count; i++)
            list.Add(i);
        return list;
    }

    private static void AssertSame<T>(List<T> expected, SegmentedList<T> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.True(actual.Capacity >= actual.Count);
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < expected.Count; i++)
        {
            if (!comparer.Equals(expected[i], actual[i]))
                Assert.Fail($"Indexer mismatch at {i}: expected {expected[i]}, got {actual[i]}.");
        }
        var index = 0;
        foreach (var item in actual)
        {
            if (!comparer.Equals(expected[index], item))
                Assert.Fail($"Enumerator mismatch at {index}.");
            index++;
        }
        Assert.Equal(expected.Count, index);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    #region Construction and growth
    [Fact]
    public void DefaultSegmentSizesKeepSegmentsOffLargeObjectHeap()
    {
        Assert.Equal(16, new SegmentedList<byte>().MinSegmentSize);
        Assert.Equal(1 << 16, new SegmentedList<byte>().MaxSegmentSize);
        Assert.Equal(1 << 14, new SegmentedList<int>().MaxSegmentSize);
        Assert.Equal(1 << 13, new SegmentedList<long>().MaxSegmentSize);
        Assert.Equal(IntPtr.Size == 8 ? 1 << 13 : 1 << 14, new SegmentedList<string>().MaxSegmentSize);
        Assert.Equal(1 << 11, new SegmentedList<(long, long, long, long)>().MaxSegmentSize);
    }

    [Fact]
    public void DefaultMinSegmentSizeIsCappedByMax()
    {
        var list = new SegmentedList<Huge>();
        Assert.Equal(8, list.MaxSegmentSize);
        Assert.Equal(8, list.MinSegmentSize);
    }

    [Fact]
    public void SegmentSizesAreRoundedUpToPowersOfTwo()
    {
        var list = new SegmentedList<int>(3, 13);
        Assert.Equal(4, list.MinSegmentSize);
        Assert.Equal(16, list.MaxSegmentSize);
    }

    [Fact]
    public void ConstructorValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedList<int>(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedList<int>(8, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedList<int>(1, (1 << 30) + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedList<int>(1, 4, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedList<int>(-1));
        Assert.Throws<ArgumentNullException>(() => new SegmentedList<int>((IEnumerable<int>)null));
    }

    [Fact]
    public void CapacityConstructorPreallocates()
    {
        var list = new SegmentedList<int>(100);
        Assert.Empty(list);
        Assert.True(list.Capacity >= 100);

        var configured = new SegmentedList<int>(4, 16, 30);
        Assert.Equal(44, configured.Capacity);
        Assert.Equal(4, configured.SegmentCount);
    }

    [Fact]
    public void CollectionConstructorCopiesElements()
    {
        var list = new SegmentedList<int>(Enumerable.Range(0, 1000));
        AssertSame(Enumerable.Range(0, 1000).ToList(), list);
    }

    [Fact]
    public void CollectionExpressionPopulatesList()
    {
        SegmentedList<int> list = [1, 2, 3];
        Assert.Equal([1, 2, 3], list.ToArray());
    }

    [Fact]
    public void GrowingSegmentsDoubleUntilMaxSize()
    {
        var list = new SegmentedList<int>(4, 32);
        var capacities = new List<int>();
        for (var i = 0; i < 100; i++)
        {
            list.Add(i);
            if (capacities.Count == 0 || capacities[^1] != list.Capacity)
                capacities.Add(list.Capacity);
        }
        Assert.Equal([4, 12, 28, 60, 92, 124], capacities);
    }

    [Fact]
    public void FixedSegmentsGrowByConstantSize()
    {
        var list = new SegmentedList<int>(8, 8);
        var capacities = new List<int>();
        for (var i = 0; i < 25; i++)
        {
            list.Add(i);
            if (capacities.Count == 0 || capacities[^1] != list.Capacity)
                capacities.Add(list.Capacity);
        }
        Assert.Equal([8, 16, 24, 32], capacities);
    }

    [Fact]
    public void EnsureCapacityAllocatesWithoutChangingCount()
    {
        var list = Filled(4, 16, 3);
        Assert.Equal(44, list.EnsureCapacity(40));
        Assert.Equal(44, list.Capacity);
        Assert.Equal(3, list.Count);
        Assert.Equal(44, list.EnsureCapacity(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.EnsureCapacity(-1));

        for (var i = 3; i < 44; i++)
            list.Add(i);
        Assert.Equal(44, list.Capacity);
        AssertSame(Enumerable.Range(0, 44).ToList(), list);
    }

    [Fact]
    public void ElementsDoNotMoveWhenListGrows()
    {
        var list = new SegmentedList<int>(1, 4) { 1 };
        ref var first = ref list[0];
        list.AddRange(new int[10_000]);
        first = 42;
        Assert.Equal(42, list[0]);
    }

    [Fact]
    public void AllocateSegmentReturningShortArrayThrows()
    {
        using var list = new PooledSegmentedList<int>(4, 4, pool: new CapturingPool<int>(shortBy: 1));
        Assert.Throws<InvalidOperationException>(() => list.Add(1));
    }
    #endregion

    #region Indexing and adding
    [Fact]
    public void AddRangeRejectsCollectionsThatWouldOverflowCount()
    {
        var list = Filled(4, 16, 1);
        Assert.Throws<InvalidOperationException>(() => list.AddRange(new HugeCollection<int>(int.MaxValue)));
        Assert.Throws<InvalidOperationException>(() => list.AddRange(new HugeReadOnlyCollection<int>(int.MaxValue)));
        Assert.Equal([0], list.ToArray());
    }

#if NETCOREAPP
    [Fact]
    public void SpanRangesRejectLengthsThatWouldOverflowCount()
    {
        var list = Filled(4, 16, 1);
        var dummy = 0;
        Assert.Throws<InvalidOperationException>(() => list.AddRange(MemoryMarshal.CreateReadOnlySpan(ref dummy, int.MaxValue)));
        Assert.Throws<InvalidOperationException>(() => list.InsertRange(0, MemoryMarshal.CreateReadOnlySpan(ref dummy, int.MaxValue)));
        Assert.Equal([0], list.ToArray());
    }
#endif

    [Theory]
    [MemberData(nameof(Configs))]
    public void AddAndIndexerRoundTrip(int min, int max)
    {
        var expected = new List<int>();
        var list = new SegmentedList<int>(min, max);
        for (var i = 0; i < 1000; i++)
        {
            expected.Add(i * 3);
            list.Add(i * 3);
        }
        AssertSame(expected, list);
    }

    [Fact]
    public void IndexerReturnsWritableReference()
    {
        var list = Filled(2, 4, 20);
        list[13] = -1;
        list[0]++;
        Assert.Equal(-1, list[13]);
        Assert.Equal(1, list[0]);
    }

    [Fact]
    public void IndexerThrowsOutOfRange()
    {
        var list = Filled(4, 4, 5);
        list.EnsureCapacity(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedList<int>()[0]);
    }

    [Fact]
    public void ExplicitInterfaceMembersForwardToList()
    {
        var list = Filled(2, 4, 10);
        IList<int> ilist = list;
        IReadOnlyList<int> rolist = list;
        ilist[3] = 99;
        Assert.Equal(99, ilist[3]);
        Assert.Equal(99, rolist[3]);
        Assert.False(ilist.IsReadOnly);
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void AddRangeAcceptsAllSourceShapes(int min, int max)
    {
        var expected = new List<int>();
        var list = new SegmentedList<int>(min, max);

        void Both(IEnumerable<int> items)
        {
            var copy = items.ToArray();
            expected.AddRange(copy);
            list.AddRange(items);
            AssertSame(expected, list);
        }

        list.AddRange(ReadOnlySpan<int>.Empty);
        list.AddRange(Enumerable.Range(0, 37).ToArray().AsSpan());
        expected.AddRange(Enumerable.Range(0, 37));
        AssertSame(expected, list);

        Both(Enumerable.Range(100, 50).ToArray());
        Both(Enumerable.Range(200, 70).ToList());
        Both(Yield(1, 2, 3, 4, 5, 6, 7));
        Both(new HashSet<int>(Enumerable.Range(300, 40)));
        Both(new ReadOnlyOnly<int>(Enumerable.Range(400, 33)));
        Both(Filled(2, 8, 45));
        Both([]);

        expected.AddRange(expected.ToArray());
        list.AddRange(list);
        AssertSame(expected, list);

        Assert.Throws<ArgumentNullException>(() => list.AddRange((IEnumerable<int>)null));
    }
    #endregion

    #region Insert
    [Theory]
    [MemberData(nameof(Configs))]
    public void InsertShiftsElementsAcrossSegments(int min, int max)
    {
        var expected = Enumerable.Range(0, 100).ToList();
        var list = Filled(min, max, 100);

        foreach (var index in new[] { 0, 50, 1, 102, 17, 104, 64 })
        {
            expected.Insert(index, -index - 1);
            list.Insert(index, -index - 1);
            AssertSame(expected, list);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => list.Insert(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Insert(list.Count + 1, 0));
    }

    [Fact]
    public void InsertIntoEmptyList()
    {
        var list = new SegmentedList<int>(1, 2);
        list.Insert(0, 1);
        list.Insert(0, 0);
        list.Insert(2, 2);
        Assert.Equal([0, 1, 2], list.ToArray());
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void InsertRangeAcceptsAllSourceShapes(int min, int max)
    {
        var expected = Enumerable.Range(0, 60).ToList();
        var list = Filled(min, max, 60);

        void Both(int index, IEnumerable<int> items)
        {
            var copy = items.ToArray();
            expected.InsertRange(index, copy);
            list.InsertRange(index, items);
            AssertSame(expected, list);
        }

        list.InsertRange(5, ReadOnlySpan<int>.Empty);
        list.InsertRange(7, Enumerable.Range(1000, 25).ToArray().AsSpan());
        expected.InsertRange(7, Enumerable.Range(1000, 25));
        AssertSame(expected, list);

        Both(0, Enumerable.Range(2000, 13).ToArray());
        Both(expected.Count, Enumerable.Range(3000, 9).ToList());
        Both(40, Yield(-1, -2, -3, -4, -5));
        Both(3, new HashSet<int>(Enumerable.Range(4000, 70)));
        Both(expected.Count / 2, Filled(1, 4, 21));
        Both(11, []);

        expected.InsertRange(19, expected.ToArray());
        list.InsertRange(19, list);
        AssertSame(expected, list);

        Assert.Throws<ArgumentOutOfRangeException>(() => list.InsertRange(-1, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.InsertRange(list.Count + 1, Yield(1)));
        Assert.Throws<ArgumentNullException>(() => list.InsertRange(0, (IEnumerable<int>)null));
    }
    #endregion

    #region Remove
    [Theory]
    [MemberData(nameof(Configs))]
    public void RemoveAtShiftsElementsAcrossSegments(int min, int max)
    {
        var expected = Enumerable.Range(0, 100).ToList();
        var list = Filled(min, max, 100);

        foreach (var index in new[] { 99, 0, 50, 13, 95, 1, 64 })
        {
            expected.RemoveAt(index);
            list.RemoveAt(index);
            AssertSame(expected, list);
        }
        while (expected.Count > 0)
        {
            expected.RemoveAt(expected.Count - 1);
            list.RemoveAt(list.Count - 1);
        }
        AssertSame(expected, list);

        list.Add(7);
        Assert.Equal([7], list.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(-1));
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void RemoveRangeAndTruncate(int min, int max)
    {
        var expected = Enumerable.Range(0, 200).ToList();
        var list = Filled(min, max, 200);

        expected.RemoveRange(10, 37);
        list.RemoveRange(10, 37);
        AssertSame(expected, list);

        expected.RemoveRange(0, 5);
        list.RemoveRange(0, 5);
        AssertSame(expected, list);

        list.RemoveRange(20, 0);
        AssertSame(expected, list);

        expected.RemoveRange(100, expected.Count - 100);
        list.Truncate(100);
        AssertSame(expected, list);

        list.Truncate(100);
        AssertSame(expected, list);

        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveRange(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveRange(0, -1));
        Assert.Throws<ArgumentException>(() => list.RemoveRange(90, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Truncate(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Truncate(-1));

        list.Truncate(0);
        Assert.Empty(list);
        list.Add(1);
        Assert.Equal([1], list.ToArray());
    }

    [Fact]
    public void RemoveRemovesFirstOccurrence()
    {
        var list = new SegmentedList<int>(1, 2) { 1, 2, 3, 2, 1 };
        Assert.True(list.Remove(2));
        Assert.Equal([1, 3, 2, 1], list.ToArray());
        Assert.False(list.Remove(9));
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void RemoveAllCompactsAcrossSegments(int min, int max)
    {
        var expected = Enumerable.Range(0, 500).ToList();
        var list = Filled(min, max, 500);

        Assert.Equal(expected.RemoveAll(static x => x % 3 == 1), list.RemoveAll(static x => x % 3 == 1));
        AssertSame(expected, list);
        Assert.Equal(0, list.RemoveAll(static x => x < 0));
        Assert.Equal(expected.RemoveAll(static x => x < 5), list.RemoveAll(static x => x < 5));
        AssertSame(expected, list);
        Assert.Equal(expected.Count, list.RemoveAll(static _ => true));
        Assert.Empty(list);
        Assert.Throws<ArgumentNullException>(() => list.RemoveAll(null));
    }

    [Fact]
    public void ClearKeepsCapacity()
    {
        var list = Filled(4, 16, 100);
        var capacity = list.Capacity;
        list.Clear();
        Assert.Empty(list);
        Assert.Equal(capacity, list.Capacity);
        list.Add(5);
        Assert.Equal([5], list.ToArray());
    }

    [Fact]
    public void RemovalsClearVacatedReferenceSlots()
    {
        var pool = new CapturingPool<string>();
        using var list = new PooledSegmentedList<string>(2, 8, pool: pool);
        for (var i = 0; i < 100; i++)
            list.Add(i.ToString());
        Assert.Equal(100, pool.NonNullSlots());

        list.RemoveAt(99);
        Assert.Equal(99, pool.NonNullSlots());
        list.RemoveAt(10);
        Assert.Equal(98, pool.NonNullSlots());
        list.RemoveRange(5, 20);
        Assert.Equal(78, pool.NonNullSlots());
        list.Truncate(60);
        Assert.Equal(60, pool.NonNullSlots());
        list.RemoveAll(static s => s.EndsWith("7"));
        Assert.Equal(list.Count, pool.NonNullSlots());
        list.Clear();
        Assert.Equal(0, pool.NonNullSlots());
    }

    [Fact]
    public void TrimExcessReleasesUnusedSegments()
    {
        var pool = new CapturingPool<string>();
        using var list = new PooledSegmentedList<string>(4, 16, pool: pool);
        list.EnsureCapacity(100);
        Assert.Equal(8, list.SegmentCount);
        for (var i = 0; i < 10; i++)
            list.Add(i.ToString());

        list.TrimExcess();
        Assert.Equal(2, list.SegmentCount);
        Assert.Equal(12, list.Capacity);
        Assert.Equal(pool.Allocated.Skip(2), pool.Released);

        list.TrimExcess();
        Assert.Equal(6, pool.Released.Count);

        for (var i = 10; i < 50; i++)
            list.Add(i.ToString());
        Assert.Equal(Enumerable.Range(0, 50).Select(static i => i.ToString()), list);

        list.Clear();
        list.TrimExcess();
        Assert.Equal(0, list.SegmentCount);
        Assert.Equal(0, list.Capacity);
        Assert.Equal(pool.Allocated.Count, pool.Released.Count);

        list.Add("x");
        Assert.Equal(["x"], list.ToArray());
    }
    #endregion

    #region Search
    [Fact]
    public void RangedSearchesRejectOutOfRangeStartIndex()
    {
        var list = Filled(2, 8, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => list.IndexOf(0, 6, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.LastIndexOf(0, 5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.FindIndex(6, 0, static _ => true));
    }

    [Fact]
    public void BackwardSearchesOnEmptyListMatchList()
    {
        var list = new SegmentedList<int>();
        var reference = new List<int>();
        Assert.Equal(reference.LastIndexOf(1, 5, 2), list.LastIndexOf(1, 5, 2));
        Assert.Equal(reference.FindLastIndex(-1, 0, static _ => true), list.FindLastIndex(-1, 0, static _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => reference.FindLastIndex(0, 0, static _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(0, 0, static _ => true));
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void IndexOfAndLastIndexOfMatchList(int min, int max)
    {
        var expected = Enumerable.Range(0, 300).Select(static i => i % 17).ToList();
        var list = new SegmentedList<int>(min, max);
        list.AddRange(expected);

        for (var value = -1; value < 18; value++)
        {
            Assert.Equal(expected.IndexOf(value), list.IndexOf(value));
            Assert.Equal(expected.LastIndexOf(value), list.LastIndexOf(value));
            Assert.Equal(expected.Contains(value), list.Contains(value));
            Assert.Equal(expected.IndexOf(value, 40), list.IndexOf(value, 40));
            Assert.Equal(expected.IndexOf(value, 33, 100), list.IndexOf(value, 33, 100));
            Assert.Equal(expected.LastIndexOf(value, 250), list.LastIndexOf(value, 250));
            Assert.Equal(expected.LastIndexOf(value, 250, 100), list.LastIndexOf(value, 250, 100));
        }

        Assert.Equal(-1, list.IndexOf(0, 300));
        Assert.Equal(-1, list.IndexOf(0, 10, 0));
        Assert.Equal(-1, list.LastIndexOf(0, 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.IndexOf(0, 301));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.IndexOf(0, 10, 291));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.LastIndexOf(0, 300));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.LastIndexOf(0, 10, 12));
        Assert.Equal(-1, new SegmentedList<int>().LastIndexOf(0));
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void PredicateSearchesMatchList(int min, int max)
    {
        var expected = Enumerable.Range(0, 300).ToList();
        var list = Filled(min, max, 300);
        Predicate<int> none = static x => x < 0;

        foreach (var match in new Predicate<int>[] { static x => x % 29 == 5, static x => x == 0, static x => x == 299, none })
        {
            Assert.Equal(expected.Find(match), list.Find(match));
            Assert.Equal(expected.FindLast(match), list.FindLast(match));
            Assert.Equal(expected.FindIndex(match), list.FindIndex(match));
            Assert.Equal(expected.FindIndex(100, match), list.FindIndex(100, match));
            Assert.Equal(expected.FindIndex(50, 120, match), list.FindIndex(50, 120, match));
            Assert.Equal(expected.FindLastIndex(match), list.FindLastIndex(match));
            Assert.Equal(expected.FindLastIndex(200, match), list.FindLastIndex(200, match));
            Assert.Equal(expected.FindLastIndex(250, 120, match), list.FindLastIndex(250, 120, match));
            Assert.Equal(expected.Exists(match), list.Exists(match));
            Assert.Equal(expected.TrueForAll(match), list.TrueForAll(match));
            Assert.Equal(expected.FindAll(match), list.FindAll(match));
        }
        Assert.True(list.TrueForAll(static x => x >= 0));

        var empty = new SegmentedList<int>();
        Assert.Equal(-1, empty.FindLastIndex(none));
        Assert.Equal(-1, empty.FindIndex(none));
        Assert.Equal(0, empty.Find(none));

        Assert.Throws<ArgumentNullException>(() => list.FindIndex(null));
        Assert.Throws<ArgumentNullException>(() => list.FindLastIndex(null));
        Assert.Throws<ArgumentNullException>(() => list.TrueForAll(null));
        Assert.Throws<ArgumentNullException>(() => list.FindAll(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.FindIndex(301, none));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.FindIndex(10, 291, none));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(300, none));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(10, 12, none));
    }

    [Fact]
    public void FindAllPreservesSegmentSizes()
    {
        var list = Filled(2, 32, 100);
        var found = list.FindAll(static x => x % 2 == 0);
        Assert.Equal(2, found.MinSegmentSize);
        Assert.Equal(32, found.MaxSegmentSize);
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void BinarySearchMatchesList(int min, int max)
    {
        var expected = Enumerable.Range(0, 200).Select(static i => i * 2).ToList();
        var list = new SegmentedList<int>(min, max);
        list.AddRange(expected);
        var descending = Comparer<int>.Create(static (a, b) => b.CompareTo(a));

        for (var value = -1; value < 402; value++)
        {
            Assert.Equal(expected.BinarySearch(value), list.BinarySearch(value));
            Assert.Equal(expected.BinarySearch(30, 90, value, null), list.BinarySearch(30, 90, value, null));
        }

        expected.Reverse();
        list.Reverse();
        Assert.Equal(expected.BinarySearch(100, descending), list.BinarySearch(100, descending));
        Assert.Equal(expected.BinarySearch(101, descending), list.BinarySearch(101, descending));
        Assert.Throws<ArgumentException>(() => list.BinarySearch(150, 51, 0, null));
    }
    #endregion

    #region Reordering
    [Theory]
    [MemberData(nameof(Configs))]
    public void ReverseMatchesList(int min, int max)
    {
        var expected = Enumerable.Range(0, 157).ToList();
        var list = Filled(min, max, 157);

        expected.Reverse();
        list.Reverse();
        AssertSame(expected, list);

        foreach (var (index, count) in new[] { (0, 1), (3, 2), (10, 51), (1, 156), (100, 57), (40, 0) })
        {
            expected.Reverse(index, count);
            list.Reverse(index, count);
            AssertSame(expected, list);
        }

        Assert.Throws<ArgumentException>(() => list.Reverse(100, 58));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Reverse(-1, 2));
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void SortMatchesList(int min, int max)
    {
        var random = new Random(min * 31 + max);
        var expected = Enumerable.Range(0, 400).Select(_ => random.Next(1000)).ToList();
        var list = new SegmentedList<int>(min, max);
        list.AddRange(expected);

        expected.Sort(0, 3, null);
        list.Sort(0, 3, null);
        AssertSame(expected, list);

        expected.Sort(50, 200, null);
        list.Sort(50, 200, null);
        AssertSame(expected, list);

        var descending = Comparer<int>.Create(static (a, b) => b.CompareTo(a));
        expected.Sort(descending);
        list.Sort(descending);
        AssertSame(expected, list);

        expected.Sort(static (a, b) => a % 10 != b % 10 ? (a % 10).CompareTo(b % 10) : a.CompareTo(b));
        list.Sort(static (a, b) => a % 10 != b % 10 ? (a % 10).CompareTo(b % 10) : a.CompareTo(b));
        AssertSame(expected, list);

        expected.Sort();
        list.Sort();
        AssertSame(expected, list);

        Assert.Throws<ArgumentException>(() => list.Sort(300, 101, null));
        Assert.Throws<ArgumentNullException>(() => list.Sort((Comparison<int>)null));
    }
    #endregion

    #region Copying
    [Theory]
    [MemberData(nameof(Configs))]
    public void CopyToOverloadsMatchList(int min, int max)
    {
        var expected = Enumerable.Range(0, 123).ToList();
        var list = Filled(min, max, 123);

        var a = new int[130];
        var b = new int[130];
        expected.CopyTo(a, 5);
        list.CopyTo(b, 5);
        Assert.Equal(a, b);

        a = new int[123];
        b = new int[123];
        expected.CopyTo(a);
        list.CopyTo(b);
        Assert.Equal(a, b);

        a = new int[50];
        b = new int[50];
        expected.CopyTo(17, a, 3, 40);
        list.CopyTo(17, b, 3, 40);
        Assert.Equal(a, b);

        var span = new int[125];
        list.CopyTo(span.AsSpan());
        Assert.Equal(expected, span.Take(123));

        Assert.ThrowsAny<ArgumentException>(() => list.CopyTo(new int[122]));
        Assert.ThrowsAny<ArgumentException>(() => list.CopyTo(new int[123], 1));
        Assert.Throws<ArgumentException>(() => list.CopyTo(new int[122].AsSpan()));
        Assert.Throws<ArgumentException>(() => list.CopyTo(100, new int[50], 0, 24));
        Assert.Throws<ArgumentNullException>(() => list.CopyTo(null, 0));
    }

    [Fact]
    public void ToArrayOfEmptyListIsEmpty() => Assert.Empty(new SegmentedList<int>().ToArray());

    [Theory]
    [MemberData(nameof(Configs))]
    public void GetRangeCopiesElements(int min, int max)
    {
        var list = Filled(min, max, 200);
        var range = list.GetRange(37, 111);
        Assert.Equal(Enumerable.Range(37, 111), range);
        Assert.Equal(list.MinSegmentSize, range.MinSegmentSize);
        Assert.Equal(list.MaxSegmentSize, range.MaxSegmentSize);
        Assert.Empty(list.GetRange(200, 0));
        Assert.Throws<ArgumentException>(() => list.GetRange(100, 101));
    }
    #endregion

    #region Enumeration
    [Fact]
    public void EnumeratorThrowsWhenListIsModified()
    {
        var list = Filled(2, 2, 10);
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var item in list)
                list.Add(item);
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in list)
                list.RemoveAt(0);
        });
    }

    [Fact]
    public void EnumeratorResetAndCurrentBehaveLikeList()
    {
        var list = Filled(2, 2, 5);
        using var enumerator = ((IEnumerable<int>)list).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current);
        enumerator.Reset();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(0, enumerator.Current);
        while (enumerator.MoveNext()) { }
        Assert.False(enumerator.MoveNext());
        Assert.Equal(0, enumerator.Current);

        Assert.Equal(Enumerable.Range(0, 5), ((IEnumerable)list).Cast<int>());
    }

    [Fact]
    public void NonGenericEnumerationYieldsBoxedElements()
    {
        var list = Filled(2, 4, 7);
        var enumerator = ((IEnumerable)list).GetEnumerator();
        var items = new List<object>();
        while (enumerator.MoveNext())
            items.Add(enumerator.Current);
        Assert.Equal(Enumerable.Range(0, 7).Cast<object>(), items);
    }

    [Fact]
    public void EnumeratorResetThrowsWhenListIsModified()
    {
        var list = Filled(2, 4, 3);
        var enumerator = list.GetEnumerator();
        list.Add(3);
        Assert.Throws<InvalidOperationException>(() => enumerator.Reset());
    }

    [Fact]
    public void EnumeratingEmptyListYieldsNothing()
    {
        var list = new SegmentedList<int>();
        list.EnsureCapacity(100);
        Assert.Empty(list);
        foreach (var _ in list.EnumerateSegments())
            Assert.Fail("No segments expected.");
    }

    [Fact]
    public void EnumerateSegmentsYieldsOccupiedPortions()
    {
        var list = Filled(4, 32, 150);
        list.EnsureCapacity(1000);
        var lengths = new List<int>();
        var items = new List<int>();
        foreach (var segment in list.EnumerateSegments())
        {
            lengths.Add(segment.Length);
            items.AddRange(segment.ToArray());
        }
        Assert.Equal([4, 8, 16, 32, 32, 32, 26], lengths);
        Assert.Equal(Enumerable.Range(0, 150), items);
    }

    [Fact]
    public void ForEachVisitsAllAndDetectsModification()
    {
        var list = Filled(1, 4, 50);
        var visited = new List<int>();
        list.ForEach(visited.Add);
        Assert.Equal(Enumerable.Range(0, 50), visited);

        Assert.Throws<InvalidOperationException>(() => list.ForEach(_ => list.Add(0)));
        Assert.Throws<ArgumentNullException>(() => list.ForEach(null));
    }
    #endregion

    [Theory]
    [MemberData(nameof(RandomConfigs))]
    public void RandomOperationsMatchList(int min, int max, int seed)
    {
        var random = new Random(seed);
        var expected = new List<int>();
        var list = new SegmentedList<int>(min, max);

        int[] Items(int maxCount) => Enumerable.Range(0, random.Next(maxCount)).Select(_ => random.Next(1000)).ToArray();

        for (var step = 0; step < 1500; step++)
        {
            var op = random.Next(16);
            switch (op)
            {
                case 0 or 1 or 2:
                {
                    var value = random.Next(1000);
                    expected.Add(value);
                    list.Add(value);
                    break;
                }
                case 3:
                {
                    var index = random.Next(expected.Count + 1);
                    var value = random.Next(1000);
                    expected.Insert(index, value);
                    list.Insert(index, value);
                    break;
                }
                case 4 when expected.Count > 0:
                {
                    var index = random.Next(expected.Count);
                    expected.RemoveAt(index);
                    list.RemoveAt(index);
                    break;
                }
                case 5:
                {
                    var items = Items(80);
                    expected.AddRange(items);
                    list.AddRange(items);
                    break;
                }
                case 6:
                {
                    var items = Items(80);
                    var index = random.Next(expected.Count + 1);
                    expected.InsertRange(index, items);
                    list.InsertRange(index, items);
                    break;
                }
                case 7:
                {
                    var items = Items(40);
                    var index = random.Next(expected.Count + 1);
                    expected.InsertRange(index, items);
                    list.InsertRange(index, items.Select(static x => x));
                    break;
                }
                case 8:
                {
                    var index = random.Next(expected.Count + 1);
                    var count = random.Next(expected.Count - index + 1);
                    expected.RemoveRange(index, count);
                    list.RemoveRange(index, count);
                    break;
                }
                case 9:
                {
                    var count = expected.Count - random.Next(Math.Min(expected.Count, 60) + 1);
                    expected.RemoveRange(count, expected.Count - count);
                    list.Truncate(count);
                    break;
                }
                case 10:
                {
                    var index = random.Next(expected.Count + 1);
                    var count = random.Next(expected.Count - index + 1);
                    expected.Reverse(index, count);
                    list.Reverse(index, count);
                    break;
                }
                case 11:
                {
                    var divisor = random.Next(20, 200);
                    Assert.Equal(expected.RemoveAll(x => x % divisor == 0), list.RemoveAll(x => x % divisor == 0));
                    break;
                }
                case 12:
                    list.TrimExcess();
                    break;
                case 13:
                {
                    var index = random.Next(expected.Count + 1);
                    var count = random.Next(expected.Count - index + 1);
                    expected.Sort(index, count, null);
                    list.Sort(index, count, null);
                    break;
                }
                case 14 when random.Next(8) == 0:
                    expected.Clear();
                    list.Clear();
                    break;
                case 15:
                    list.EnsureCapacity(expected.Count + random.Next(100));
                    break;
            }
            AssertSame(expected, list);
        }
    }

    [Fact]
    public void SegmentsHoldingSingleElementForEnormousElementType()
    {
        var list = new SegmentedList<Enormous>();
        Assert.Equal(1, list.MaxSegmentSize);
        Assert.Equal(1, list.MinSegmentSize);
        list.Add(new Enormous { B = 1 });
        list.Add(new Enormous { B = 2 });
        Assert.Equal(2, list.Count);
        Assert.Equal(2, list[1].B);
        Assert.Equal(2, list.SegmentCount);
    }

    [Fact]
    public void AddThrowsWhenCapacityIsExhausted()
    {
        var list = new SegmentedList<int>();
        SetField(list, "_capacity", int.MaxValue);
        Assert.Throws<InvalidOperationException>(() => list.Add(1));
    }

    [Fact]
    public void AllocateSegmentReturningNullThrows()
    {
        using var list = new PooledSegmentedList<int>(4, 4, pool: new NullPool<int>());
        Assert.Throws<InvalidOperationException>(() => list.Add(1));
    }

    [Fact]
    public void RangeAdditionsRejectOverflowingCount()
    {
        var other = Filled(4, 16, 3);
        var list = new SegmentedList<int>();
        SetField(list, "_count", int.MaxValue - 2);
        Assert.Throws<InvalidOperationException>(() => list.AddRange(other));
        Assert.Throws<InvalidOperationException>(() => list.AddRange(new ReadOnlySpan<int>([1, 2, 3])));
        Assert.Throws<InvalidOperationException>(() => list.InsertRange(0, new ReadOnlySpan<int>([1, 2, 3])));
    }

    [Fact]
    public void InsertRangeEnumerableHandlesEmptyAndAppendingSources()
    {
        var list = Filled(2, 4, 6);
        list.InsertRange(3, Yield());
        Assert.Equal([0, 1, 2, 3, 4, 5], list.ToArray());
        list.InsertRange(6, Yield(6, 7));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], list.ToArray());
        list.InsertRange(0, Yield());
        Assert.Equal(8, list.Count);
    }

    [Fact]
    public void InsertRangeEnumerableKeepsPartialInsertAtIndexWhenSourceThrows()
    {
        static IEnumerable<int> Failing()
        {
            yield return 100;
            yield return 101;
            throw new InvalidOperationException("boom");
        }

        var expected = Enumerable.Range(0, 10).ToList();
        var list = Filled(2, 4, 10);
        Assert.Throws<InvalidOperationException>(() => expected.InsertRange(3, Failing()));
        var ex = Assert.Throws<InvalidOperationException>(() => list.InsertRange(3, Failing()));
        Assert.Equal("boom", ex.Message);
        Assert.Equal(expected, list);
    }

    [Fact]
    public void BackwardSearchesRejectEachInvalidCountOperand()
    {
        var list = Filled(2, 4, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => list.LastIndexOf(0, 5, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.LastIndexOf(0, 5, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(5, -1, static _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(5, 7, static _ => true));
    }
}
