using System.Buffers;

using LaquaiLib.Buffers.Collections;

namespace LaquaiLib.Buffers.Tests.Collections;

public class PooledSegmentedListTests
{
    private sealed class TrackingPool<T>(int extraLength = 0, T fill = default) : ArrayPool<T>
    {
        public readonly List<int> Requests = [];
        public readonly List<(T[] Array, bool Cleared)> Returns = [];
        public readonly HashSet<T[]> Outstanding = [];

        public override T[] Rent(int minimumLength)
        {
            Requests.Add(minimumLength);
            var array = new T[minimumLength + extraLength];
            Array.Fill(array, fill);
            Outstanding.Add(array);
            return array;
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            Assert.True(Outstanding.Remove(array), "Returned an array that was not rented or was already returned.");
            Returns.Add((array, Array.TrueForAll(array, static x => EqualityComparer<T>.Default.Equals(x, default))));
        }
    }

    [Fact]
    public void RentsSegmentsFollowingGrowthSchedule()
    {
        var pool = new TrackingPool<int>();
        using var list = new PooledSegmentedList<int>(4, 16, pool: pool);
        for (var i = 0; i < 40; i++)
            list.Add(i);

        Assert.Equal([4, 8, 16, 16], pool.Requests);
        Assert.Equal(44, list.Capacity);
        Assert.Equal(Enumerable.Range(0, 40), list);
    }

    [Fact]
    public void ConstructorsUseProvidedPool()
    {
        var pool = new TrackingPool<int>();
        using (var list = new PooledSegmentedList<int>(100, pool))
            Assert.True(list.Capacity >= 100);
        using (var list = new PooledSegmentedList<int>(Enumerable.Range(0, 50), pool))
            Assert.Equal(Enumerable.Range(0, 50), list);
        using (var list = new PooledSegmentedList<int>(2, 2, 7, pool))
            Assert.Equal(8, list.Capacity);
        using (var list = new PooledSegmentedList<int>(pool))
            list.Add(1);

        Assert.NotEmpty(pool.Requests);
        Assert.Empty(pool.Outstanding);
    }

    [Fact]
    public void DefaultConstructorRentsFromSharedPool()
    {
        using var list = new PooledSegmentedList<int>();
        list.AddRange(Enumerable.Range(0, 100_000).ToArray());
        Assert.Equal(Enumerable.Range(0, 100_000), list);
    }

    [Fact]
    public void NullPoolFallsBackToSharedPool()
    {
        using var list = new PooledSegmentedList<int>(4, 16, 10, pool: null);
        list.AddRange(Enumerable.Range(0, 50).ToArray());
        Assert.Equal(Enumerable.Range(0, 50), list);
        list.Dispose();
        list.Dispose();
        Assert.Empty(list);
    }

    [Fact]
    public void OversizedAndDirtyRentedArraysDoNotLeakIntoList()
    {
        var pool = new TrackingPool<int>(extraLength: 5, fill: -1);
        using var list = new PooledSegmentedList<int>(2, 8, pool: pool);
        var expected = new List<int>();
        for (var i = 0; i < 100; i++)
        {
            list.Add(i);
            expected.Add(i);
        }
        list.RemoveRange(10, 30);
        expected.RemoveRange(10, 30);
        list.InsertRange(5, [100, 101, 102]);
        expected.InsertRange(5, [100, 101, 102]);
        list.Insert(0, 7);
        expected.Insert(0, 7);

        Assert.Equal(expected, list);
        Assert.DoesNotContain(-1, list);
        var total = 0;
        foreach (var segment in list.EnumerateSegments())
        {
            Assert.True(segment.Length <= 8);
            total += segment.Length;
        }
        Assert.Equal(expected.Count, total);
    }

    [Fact]
    public void TrimExcessReturnsUnusedSegments()
    {
        var pool = new TrackingPool<string>();
        using var list = new PooledSegmentedList<string>(4, 16, pool: pool);
        list.EnsureCapacity(100);
        for (var i = 0; i < 10; i++)
            list.Add(i.ToString());

        list.TrimExcess();
        Assert.Equal(6, pool.Returns.Count);
        Assert.All(pool.Returns, static r => Assert.True(r.Cleared));
        Assert.Equal(12, list.Capacity);
        Assert.Equal(Enumerable.Range(0, 10).Select(static i => i.ToString()), list);
    }

    [Fact]
    public void DisposeReturnsAllSegmentsClearingReferences()
    {
        var pool = new TrackingPool<string>();
        var list = new PooledSegmentedList<string>(2, 4, pool: pool);
        for (var i = 0; i < 30; i++)
            list.Add(i.ToString());

        list.Dispose();
        Assert.Empty(pool.Outstanding);
        Assert.All(pool.Returns, static r => Assert.True(r.Cleared));
        Assert.Empty(list);
        Assert.Equal(0, list.Capacity);
        Assert.Equal(0, list.SegmentCount);

        var returned = pool.Returns.Count;
        list.Dispose();
        Assert.Equal(returned, pool.Returns.Count);
    }

    [Fact]
    public void DisposeDoesNotClearPureValueTypeSegments()
    {
        var pool = new TrackingPool<int>();
        var list = new PooledSegmentedList<int>(2, 4, pool: pool);
        list.AddRange(Enumerable.Range(0, 30).ToArray());
        list.Dispose();
        Assert.NotEmpty(pool.Returns);
        Assert.All(pool.Returns, static r => Assert.False(r.Cleared));
    }

    [Fact]
    public void ClearOnReturnClearsPureValueTypeSegments()
    {
        var pool = new TrackingPool<int>();
        using (var list = new PooledSegmentedList<int>(2, 4, pool: pool, clearOnReturn: true))
        {
            list.AddRange(Enumerable.Range(1, 30).ToArray());
            list.RemoveRange(10, 20);
            list.TrimExcess();
        }
        using (var list = new PooledSegmentedList<int>(Enumerable.Range(1, 30), pool, clearOnReturn: true)) { }
        using (var list = new PooledSegmentedList<int>(30, pool, clearOnReturn: true))
            list.AddRange(Enumerable.Range(1, 30).ToArray());
        using (var list = new PooledSegmentedList<int>(pool, clearOnReturn: true))
            list.AddRange(Enumerable.Range(1, 30).ToArray());

        Assert.Empty(pool.Outstanding);
        Assert.NotEmpty(pool.Returns);
        Assert.All(pool.Returns, static r => Assert.True(r.Cleared));
    }

    [Fact]
    public void ListIsUsableAfterDispose()
    {
        var pool = new TrackingPool<int>();
        var list = new PooledSegmentedList<int>(2, 4, pool: pool);
        list.AddRange(Enumerable.Range(0, 30).ToArray());
        list.Dispose();

        list.AddRange(Enumerable.Range(0, 10).ToArray());
        Assert.Equal(Enumerable.Range(0, 10), list);
        list.Dispose();
        Assert.Empty(pool.Outstanding);
    }

    [Fact]
    public void FindAllAndGetRangeReturnUnpooledListsWithSameSegmentSizes()
    {
        var pool = new TrackingPool<int>();
        using var list = new PooledSegmentedList<int>(4, 16, pool: pool);
        for (var i = 0; i < 40; i++)
            list.Add(i);
        var rented = pool.Requests.Count;

        var evens = list.FindAll(static x => x % 2 == 0);
        var range = list.GetRange(5, 20);

        Assert.IsType<SegmentedList<int>>(evens);
        Assert.IsType<SegmentedList<int>>(range);
        Assert.Equal(rented, pool.Requests.Count);
        Assert.Equal((4, 16), (evens.MinSegmentSize, evens.MaxSegmentSize));
        Assert.Equal((4, 16), (range.MinSegmentSize, range.MaxSegmentSize));
        Assert.Equal(Enumerable.Range(0, 20).Select(static x => x * 2), evens);
        Assert.Equal(Enumerable.Range(5, 20), range);
    }
}
