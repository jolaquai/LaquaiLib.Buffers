using System.Buffers;
using System.Collections;
#if NETCOREAPP
using System.Numerics;
#endif

using LaquaiLib.Buffers.Extensions;

namespace LaquaiLib.Buffers.Collections;

/// <summary>
/// Represents a list of <typeparamref name="T"/> stored across a chain of arrays (segments) instead of one contiguous array.
/// Growing appends segments rather than copying existing elements, so growth never moves elements in memory and no single allocation exceeds <see cref="MaxSegmentSize"/> elements.
/// </summary>
/// <remarks>
/// The first segment holds <see cref="MinSegmentSize"/> elements and each subsequent segment doubles in size until <see cref="MaxSegmentSize"/> is reached; every segment after that is <see cref="MaxSegmentSize"/> elements long.
/// Passing equal minimum and maximum sizes yields fixed-size segments. Both sizes are rounded up to powers of two, so any index maps to its segment in constant time.
/// Unless configured, <see cref="MaxSegmentSize"/> defaults to the largest power of two that keeps a segment off the large object heap, and <see cref="MinSegmentSize"/> to 16 (or <see cref="MaxSegmentSize"/> if that is smaller).
/// </remarks>
/// <typeparam name="T">The type of elements in the list.</typeparam>
public closed class SegmentedListBase<T> : IList<T>, IReadOnlyList<T>
{
    private const int LargeObjectHeapThreshold = 85000;
    private const int MaxSegmentShift = 30;
    private static readonly int DefaultMaxShift = GetDefaultMaxShift();
    private static readonly int DefaultMinShift = Math.Min(4, DefaultMaxShift);

    private readonly int _minShift;
    private readonly int _maxShift;
    private readonly int _growingSegments;
    private readonly int _growingCapacity;

    private T[][] _segments = [];
    private int _segmentCount;
    private int _count;
    private int _capacity;
    private int _version;

    // _count == SegmentStart(_tailSegment) + _tailOffset; while _count > 0, _tailSegment is the segment holding the last element
    private T[] _tail;
    private int _tailSegment = -1;
    private int _tailOffset;
    private int _tailLength;

    /// <summary>
    /// Initializes a new, empty <see cref="SegmentedListBase{T}"/> with the default segment sizes.
    /// </summary>
    protected SegmentedListBase() : this(1 << DefaultMinShift, 1 << DefaultMaxShift) { }
    /// <summary>
    /// Initializes a new <see cref="SegmentedListBase{T}"/> with the default segment sizes and room for at least <paramref name="capacity"/> elements.
    /// </summary>
    /// <param name="capacity">The minimum number of elements the list can hold without allocating.</param>
    protected SegmentedListBase(int capacity) : this() => EnsureCapacity(capacity);
    /// <summary>
    /// Initializes a new <see cref="SegmentedListBase{T}"/> with the default segment sizes containing the elements of <paramref name="collection"/>.
    /// </summary>
    /// <param name="collection">The collection whose elements are copied into the list.</param>
    protected SegmentedListBase(IEnumerable<T> collection) : this() => AddRange(collection);
    /// <summary>
    /// Initializes a new <see cref="SegmentedListBase{T}"/> with the specified segment sizes.
    /// </summary>
    /// <param name="minSegmentSize">The size of the first segment. Rounded up to a power of two.</param>
    /// <param name="maxSegmentSize">The size segments stop growing at. Rounded up to a power of two; must not exceed 2^30. Equal to <paramref name="minSegmentSize"/> for fixed-size segments.</param>
    /// <param name="capacity">The minimum number of elements the list can hold without allocating.</param>
    protected SegmentedListBase(int minSegmentSize, int maxSegmentSize, int capacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minSegmentSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSegmentSize, 1 << MaxSegmentShift);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSegmentSize, minSegmentSize);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _minShift = CeilLog2(minSegmentSize);
        _maxShift = CeilLog2(maxSegmentSize);
        _growingSegments = _maxShift - _minShift;
        _growingCapacity = ((1 << _growingSegments) - 1) << _minShift;

        if (capacity > 0)
            EnsureCapacity(capacity);
    }

    /// <summary>
    /// Gets the number of elements in the list.
    /// </summary>
    public int Count => _count;
    /// <summary>
    /// Gets the number of elements the list can hold without allocating another segment.
    /// </summary>
    public int Capacity => _capacity;
    /// <summary>
    /// Gets the size of the first segment.
    /// </summary>
    public int MinSegmentSize => 1 << _minShift;
    /// <summary>
    /// Gets the size segments stop growing at.
    /// </summary>
    public int MaxSegmentSize => 1 << _maxShift;
    /// <summary>
    /// Gets the number of segments currently allocated, including ones that hold no elements.
    /// </summary>
    public int SegmentCount => _segmentCount;

    /// <summary>
    /// Gets a reference to the element at <paramref name="index"/>.
    /// The reference remains valid across growth, but not across operations that shift or remove elements.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count)
                ThrowIndexOutOfRange(index);
            var segment = Locate(index, out var offset);
            return ref At(At(_segments, segment), offset);
        }
    }

    #region Segment math
    private static int GetDefaultMaxShift()
    {
        var elements = (LargeObjectHeapThreshold - 1 - 3 * IntPtr.Size) / Unsafe.SizeOf<T>();
        return Math.Min(BitOperations.Log2((uint)elements), MaxSegmentShift);
    }
    private static int CeilLog2(int value) => value == 1 ? 0 : BitOperations.Log2((uint)(value - 1)) + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref TItem At<TItem>(TItem[] array, int index)
#if NETCOREAPP
        => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), (nint)(uint)index);
#else
        => ref array[index];
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SegmentLength(int segment) => segment < _growingSegments ? 1 << (_minShift + segment) : 1 << _maxShift;
    private long SegmentStart(int segment) => segment <= _growingSegments ? ((1L << segment) - 1) << _minShift : _growingCapacity + ((long)(segment - _growingSegments) << _maxShift);
    private int UsableLength(int segment) => (int)Math.Min(SegmentLength(segment), int.MaxValue - SegmentStart(segment));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Locate(int index, out int offset)
    {
        if ((uint)index < (uint)_growingCapacity)
        {
            var segment = BitOperations.Log2((uint)(index >> _minShift) + 1);
            offset = index - (((1 << segment) - 1) << _minShift);
            return segment;
        }
        index -= _growingCapacity;
        offset = index & ((1 << _maxShift) - 1);
        return _growingSegments + (index >> _maxShift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ChunkAt(int index, int max, out T[] array, out int offset)
    {
        var segment = Locate(index, out offset);
        array = _segments[segment];
        return Math.Min(SegmentLength(segment) - offset, max);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ChunkBefore(int end, int max, out T[] array, out int offset)
    {
        var segment = Locate(end - 1, out offset);
        array = _segments[segment];
        var length = Math.Min(offset + 1, max);
        offset -= length - 1;
        return length;
    }
    #endregion

    #region Storage management
    /// <summary>
    /// Allocates the array backing a new segment.
    /// </summary>
    /// <param name="length">The number of elements the segment addresses. The returned array must be at least this long.</param>
    /// <returns>The allocated array.</returns>
    protected virtual T[] AllocateSegment(int length)
#if NETCOREAPP
        => GC.AllocateUninitializedArray<T>(length);
#else
        => new T[length];
#endif
    /// <summary>
    /// Releases the array backing a segment the list no longer uses.
    /// </summary>
    /// <param name="segment">The array previously returned by <see cref="AllocateSegment(int)"/>.</param>
    protected virtual void ReleaseSegment(T[] segment) { }
    /// <summary>
    /// Creates a new <see cref="SegmentedListBase{T}"/> of the same type and segment sizes, left empty.
    /// </summary>
    protected abstract SegmentedListBase<T> EmptyFromThis();

    private void AddSegment()
    {
        if (_capacity == int.MaxValue)
            ThrowCapacityExceeded();

        var segment = _segmentCount;
        var length = SegmentLength(segment);
        var array = AllocateSegment(length);
        if (array is null || array.Length < length)
            throw new InvalidOperationException($"{nameof(AllocateSegment)} must return an array of at least the requested length.");

        if (segment == _segments.Length)
            Array.Resize(ref _segments, Math.Max(4, segment * 2));
        _segments[segment] = array;
        _segmentCount = segment + 1;
        _capacity = (int)Math.Min((long)_capacity + length, int.MaxValue);
    }

    private void AdvanceTail()
    {
        var next = _tailSegment + 1;
        if (next == _segmentCount)
            AddSegment();
        _tail = _segments[next];
        _tailSegment = next;
        _tailOffset = 0;
        _tailLength = UsableLength(next);
    }

    private void SyncTail()
    {
        if (_count == 0)
        {
            _tail = null;
            _tailSegment = -1;
            _tailOffset = 0;
            _tailLength = 0;
            return;
        }
        var segment = Locate(_count - 1, out var offset);
        _tail = _segments[segment];
        _tailSegment = segment;
        _tailOffset = offset + 1;
        _tailLength = UsableLength(segment);
    }

    /// <summary>
    /// Ensures the list can hold at least <paramref name="capacity"/> elements without allocating, allocating segments as needed.
    /// </summary>
    /// <param name="capacity">The minimum capacity.</param>
    /// <returns>The new capacity.</returns>
    public int EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        while (_capacity < capacity)
            AddSegment();
        return _capacity;
    }

    /// <summary>
    /// Releases all segments that hold no elements.
    /// </summary>
    public void TrimExcess()
    {
        var keep = _tailSegment + 1;
        while (_segmentCount > keep)
        {
            var segment = --_segmentCount;
            var array = _segments[segment];
            _segments[segment] = null;
            _capacity = (int)Math.Min(SegmentStart(segment), int.MaxValue);
            ReleaseSegment(array);
        }
        if (keep == 0)
            _segments = [];
    }

    private protected void ReleaseAll()
    {
        _count = 0;
        _version++;
        SyncTail();
        TrimExcess();
    }

    private void CopyWithin(int source, int destination, int length)
    {
        if (length == 0 || source == destination)
            return;

        if (destination < source)
        {
            while (length > 0)
            {
                var n = ChunkAt(source, length, out var sa, out var so);
                n = ChunkAt(destination, n, out var da, out var doff);
                sa.AsSpan(so, n).CopyTo(da.AsSpan(doff, n));
                source += n;
                destination += n;
                length -= n;
            }
        }
        else
        {
            var sourceEnd = source + length;
            var destinationEnd = destination + length;
            while (length > 0)
            {
                var n = ChunkBefore(sourceEnd, length, out var sa, out var so);
                var m = ChunkBefore(destinationEnd, n, out var da, out var doff);
                sa.AsSpan(so + n - m, m).CopyTo(da.AsSpan(doff, m));
                sourceEnd -= m;
                destinationEnd -= m;
                length -= m;
            }
        }
    }

    private void ClearRange(int index, int length)
    {
        while (length > 0)
        {
            var n = ChunkAt(index, length, out var array, out var offset);
            Array.Clear(array, offset, n);
            index += n;
            length -= n;
        }
    }

    private void Write(int index, ReadOnlySpan<T> source)
    {
        while (!source.IsEmpty)
        {
            var n = ChunkAt(index, source.Length, out var array, out var offset);
            source.Slice(0, n).CopyTo(array.AsSpan(offset, n));
            source = source.Slice(n);
            index += n;
        }
    }

    private void Read(int index, Span<T> destination)
    {
        while (!destination.IsEmpty)
        {
            var n = ChunkAt(index, destination.Length, out var array, out var offset);
            array.AsSpan(offset, n).CopyTo(destination);
            destination = destination.Slice(n);
            index += n;
        }
    }
    #endregion

    #region Add / Insert
    /// <summary>
    /// Adds <paramref name="item"/> to the end of the list.
    /// </summary>
    /// <param name="item">The element to add.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T item)
    {
        var offset = _tailOffset;
        if (offset < _tailLength)
        {
            At(_tail, offset) = item;
            _tailOffset = offset + 1;
            _count++;
            _version++;
        }
        else
            AddToNewSegment(item);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddToNewSegment(T item)
    {
        AdvanceTail();
        _tail[0] = item;
        _tailOffset = 1;
        _count++;
        _version++;
    }

    /// <summary>
    /// Adds the elements of <paramref name="items"/> to the end of the list.
    /// </summary>
    /// <param name="items">The elements to add.</param>
    public void AddRange(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty)
            return;
        if (items.Length > int.MaxValue - _count)
            ThrowCapacityExceeded();

        EnsureCapacity(_count + items.Length);
        _version++;
        while (true)
        {
            if (_tailOffset == _tailLength)
                AdvanceTail();
            var n = Math.Min(_tailLength - _tailOffset, items.Length);
            items.Slice(0, n).CopyTo(_tail.AsSpan(_tailOffset, n));
            _tailOffset += n;
            _count += n;
            if (n == items.Length)
                return;
            items = items.Slice(n);
        }
    }

    /// <summary>
    /// Adds the elements of <paramref name="collection"/> to the end of the list.
    /// </summary>
    /// <param name="collection">The elements to add.</param>
    public void AddRange(IEnumerable<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        switch (collection)
        {
            case T[] array:
                AddRange(new ReadOnlySpan<T>(array));
                return;
            case List<T> list:
                AddRange(CollectionsMarshal.AsSpan(list));
                return;
            case SegmentedListBase<T> other:
            {
                var remaining = other._count;
                if (remaining > int.MaxValue - _count)
                    ThrowCapacityExceeded();
                EnsureCapacity(_count + remaining);
                for (var index = 0; index < remaining;)
                {
                    var n = other.ChunkAt(index, remaining - index, out var array, out var offset);
                    AddRange(new ReadOnlySpan<T>(array, offset, n));
                    index += n;
                }
                return;
            }
            case ICollection<T> c:
                if (c.Count > int.MaxValue - _count)
                    ThrowCapacityExceeded();
                EnsureCapacity(_count + c.Count);
                break;
            case IReadOnlyCollection<T> c:
                if (c.Count > int.MaxValue - _count)
                    ThrowCapacityExceeded();
                EnsureCapacity(_count + c.Count);
                break;
        }

        foreach (var item in collection)
            Add(item);
    }

    /// <summary>
    /// Inserts <paramref name="item"/> at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">The zero-based index to insert at.</param>
    /// <param name="item">The element to insert.</param>
    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_count)
            ThrowIndexOutOfRange(index);
        if (index == _count)
        {
            Add(item);
            return;
        }

        if (_tailOffset == _tailLength)
            AdvanceTail();
        _tailOffset++;
        var oldCount = _count++;
        _version++;
        CopyWithin(index, index + 1, oldCount - index);
        var segment = Locate(index, out var offset);
        _segments[segment][offset] = item;
    }

    /// <summary>
    /// Inserts the elements of <paramref name="items"/> at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">The zero-based index to insert at.</param>
    /// <param name="items">The elements to insert. Must not refer to this list's own storage.</param>
    public void InsertRange(int index, ReadOnlySpan<T> items)
    {
        if ((uint)index > (uint)_count)
            ThrowIndexOutOfRange(index);
        if (index == _count)
        {
            AddRange(items);
            return;
        }
        if (items.IsEmpty)
            return;
        if (items.Length > int.MaxValue - _count)
            ThrowCapacityExceeded();

        EnsureCapacity(_count + items.Length);
        var oldCount = _count;
        _count += items.Length;
        _version++;
        SyncTail();
        CopyWithin(index, index + items.Length, oldCount - index);
        Write(index, items);
    }

    /// <summary>
    /// Inserts the elements of <paramref name="collection"/> at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">The zero-based index to insert at.</param>
    /// <param name="collection">The elements to insert.</param>
    public void InsertRange(int index, IEnumerable<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if ((uint)index > (uint)_count)
            ThrowIndexOutOfRange(index);

        switch (collection)
        {
            case T[] array:
                InsertRange(index, new ReadOnlySpan<T>(array));
                return;
            case List<T> list:
                InsertRange(index, CollectionsMarshal.AsSpan(list));
                return;
        }

        var oldCount = _count;
        try
        {
            AddRange(collection);
        }
        finally
        {
            var added = _count - oldCount;
            if (added != 0 && index != oldCount)
            {
                ReverseCore(index, oldCount - index);
                ReverseCore(oldCount, added);
                ReverseCore(index, _count - index);
            }
        }
    }
    #endregion

    #region Remove
    /// <summary>
    /// Removes the first occurrence of <paramref name="item"/> from the list.
    /// </summary>
    /// <param name="item">The element to remove.</param>
    /// <returns><see langword="true"/> if an element was removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(T item)
    {
        var index = IndexOf(item);
        if (index < 0)
            return false;
        RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Removes the element at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">The zero-based index of the element to remove.</param>
    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count)
            ThrowIndexOutOfRange(index);

        var last = _count - 1;
        if (index < last)
            CopyWithin(index + 1, index, last - index);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            _tail[_tailOffset - 1] = default;
        _count = last;
        _version++;
        if (--_tailOffset == 0)
            SyncTail();
    }

    /// <summary>
    /// Removes <paramref name="count"/> elements starting at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">The zero-based index of the first element to remove.</param>
    /// <param name="count">The number of elements to remove.</param>
    public void RemoveRange(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_count - index < count)
            ThrowInvalidRange();
        if (count == 0)
            return;

        var newCount = _count - count;
        CopyWithin(index + count, index, newCount - index);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            ClearRange(newCount, count);
        _count = newCount;
        _version++;
        SyncTail();
    }

    /// <summary>
    /// Removes all elements at or after <paramref name="count"/>, leaving the first <paramref name="count"/> elements. Capacity is unaffected.
    /// </summary>
    /// <param name="count">The number of elements to keep.</param>
    public void Truncate(int count)
    {
        if ((uint)count > (uint)_count)
            ThrowCountOutOfRange(count);
        RemoveRange(count, _count - count);
    }

    /// <summary>
    /// Removes all elements that match <paramref name="match"/>.
    /// </summary>
    /// <param name="match">The predicate elements to remove satisfy.</param>
    /// <returns>The number of elements removed.</returns>
    public int RemoveAll(Predicate<T> match)
    {
        var write = FindIndex(match);
        if (write < 0)
            return 0;

        var ws = Locate(write, out var wo);
        var wa = _segments[ws];
        var wl = SegmentLength(ws);
        for (var read = write + 1; read < _count;)
        {
            var n = ChunkAt(read, _count - read, out var ra, out var ro);
            for (var i = ro; i < ro + n; i++)
            {
                if (match(ra[i]))
                    continue;
                wa[wo] = ra[i];
                write++;
                if (++wo == wl && ++ws < _segmentCount)
                {
                    wa = _segments[ws];
                    wl = SegmentLength(ws);
                    wo = 0;
                }
            }
            read += n;
        }

        var removed = _count - write;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            ClearRange(write, removed);
        _count = write;
        _version++;
        SyncTail();
        return removed;
    }

    /// <summary>
    /// Removes all elements from the list. Capacity is unaffected; use <see cref="TrimExcess"/> to release segments.
    /// </summary>
    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            ClearRange(0, _count);
        _count = 0;
        _version++;
        SyncTail();
    }
    #endregion

    #region Search
    /// <summary>
    /// Determines whether the list contains <paramref name="item"/>.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    /// <returns><see langword="true"/> if <paramref name="item"/> is found; otherwise <see langword="false"/>.</returns>
    public bool Contains(T item) => IndexOf(item) >= 0;

    /// <summary>
    /// Searches for <paramref name="item"/> and returns the index of its first occurrence, or -1.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    public int IndexOf(T item) => IndexOf(item, 0, _count);
    /// <summary>
    /// Searches for <paramref name="item"/> from <paramref name="index"/> to the end of the list and returns the index of its first occurrence, or -1.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    /// <param name="index">The zero-based starting index of the search.</param>
    public int IndexOf(T item, int index)
    {
        if ((uint)index > (uint)_count)
            ThrowIndexOutOfRange(index);
        return IndexOf(item, index, _count - index);
    }
    /// <summary>
    /// Searches for <paramref name="item"/> within <paramref name="count"/> elements starting at <paramref name="index"/> and returns the index of its first occurrence, or -1.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    /// <param name="index">The zero-based starting index of the search.</param>
    /// <param name="count">The number of elements to search.</param>
    public int IndexOf(T item, int index, int count)
    {
        if ((uint)index > (uint)_count)
            ThrowIndexOutOfRange(index);
        if ((uint)count > (uint)(_count - index))
            ThrowCountOutOfRange(count);

        for (var end = index + count; index < end;)
        {
            var n = ChunkAt(index, end - index, out var array, out var offset);
            var found = Array.IndexOf(array, item, offset, n);
            if (found >= 0)
                return index + found - offset;
            index += n;
        }
        return -1;
    }

    /// <summary>
    /// Searches for <paramref name="item"/> and returns the index of its last occurrence, or -1.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    public int LastIndexOf(T item) => _count == 0 ? -1 : LastIndexOf(item, _count - 1, _count);
    /// <summary>
    /// Searches backward for <paramref name="item"/> from <paramref name="index"/> to the start of the list and returns the index of its last occurrence, or -1.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    /// <param name="index">The zero-based starting index of the backward search.</param>
    public int LastIndexOf(T item, int index)
    {
        if (index >= _count)
            ThrowIndexOutOfRange(index);
        return LastIndexOf(item, index, index + 1);
    }
    /// <summary>
    /// Searches backward for <paramref name="item"/> within <paramref name="count"/> elements ending at <paramref name="index"/> and returns the index of its last occurrence, or -1.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    /// <param name="index">The zero-based starting index of the backward search.</param>
    /// <param name="count">The number of elements to search.</param>
    public int LastIndexOf(T item, int index, int count)
    {
        if (_count == 0)
            return -1;
        if ((uint)index >= (uint)_count)
            ThrowIndexOutOfRange(index);
        if (count < 0 || count > index + 1)
            ThrowCountOutOfRange(count);

        for (int end = index + 1, stop = end - count; end > stop;)
        {
            var n = ChunkBefore(end, end - stop, out var array, out var offset);
            var found = Array.LastIndexOf(array, item, offset + n - 1, n);
            if (found >= 0)
                return end - n + found - offset;
            end -= n;
        }
        return -1;
    }

    /// <summary>
    /// Determines whether any element matches <paramref name="match"/>.
    /// </summary>
    /// <param name="match">The predicate to test elements with.</param>
    public bool Exists(Predicate<T> match) => FindIndex(match) >= 0;

    /// <summary>
    /// Determines whether every element matches <paramref name="match"/>.
    /// </summary>
    /// <param name="match">The predicate to test elements with.</param>
    public bool TrueForAll(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        for (var index = 0; index < _count;)
        {
            var n = ChunkAt(index, _count - index, out var array, out var offset);
            for (var i = offset; i < offset + n; i++)
            {
                if (!match(array[i]))
                    return false;
            }
            index += n;
        }
        return true;
    }

    /// <summary>
    /// Returns the first element that matches <paramref name="match"/>, or <see langword="default"/>.
    /// </summary>
    /// <param name="match">The predicate to test elements with.</param>
    public T Find(Predicate<T> match)
    {
        var index = FindIndex(match);
        return index < 0 ? default : this[index];
    }

    /// <summary>
    /// Returns the last element that matches <paramref name="match"/>, or <see langword="default"/>.
    /// </summary>
    /// <param name="match">The predicate to test elements with.</param>
    public T FindLast(Predicate<T> match)
    {
        var index = FindLastIndex(match);
        return index < 0 ? default : this[index];
    }

    /// <summary>
    /// Returns a new <see cref="SegmentedListBase{T}"/> with the same segment sizes containing all elements that match <paramref name="match"/>.
    /// </summary>
    /// <param name="match">The predicate to test elements with.</param>
    public SegmentedListBase<T> FindAll(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var result = EmptyFromThis();
        for (var index = 0; index < _count;)
        {
            var n = ChunkAt(index, _count - index, out var array, out var offset);
            for (var i = offset; i < offset + n; i++)
            {
                if (match(array[i]))
                    result.Add(array[i]);
            }
            index += n;
        }
        return result;
    }

    /// <summary>
    /// Returns the index of the first element that matches <paramref name="match"/>, or -1.
    /// </summary>
    /// <param name="match">The predicate to test elements with.</param>
    public int FindIndex(Predicate<T> match) => FindIndex(0, _count, match);
    /// <summary>
    /// Returns the index of the first element at or after <paramref name="startIndex"/> that matches <paramref name="match"/>, or -1.
    /// </summary>
    /// <param name="startIndex">The zero-based starting index of the search.</param>
    /// <param name="match">The predicate to test elements with.</param>
    public int FindIndex(int startIndex, Predicate<T> match)
    {
        if ((uint)startIndex > (uint)_count)
            ThrowIndexOutOfRange(startIndex);
        return FindIndex(startIndex, _count - startIndex, match);
    }
    /// <summary>
    /// Returns the index of the first element within <paramref name="count"/> elements starting at <paramref name="startIndex"/> that matches <paramref name="match"/>, or -1.
    /// </summary>
    /// <param name="startIndex">The zero-based starting index of the search.</param>
    /// <param name="count">The number of elements to search.</param>
    /// <param name="match">The predicate to test elements with.</param>
    public int FindIndex(int startIndex, int count, Predicate<T> match)
    {
        if ((uint)startIndex > (uint)_count)
            ThrowIndexOutOfRange(startIndex);
        if ((uint)count > (uint)(_count - startIndex))
            ThrowCountOutOfRange(count);
        ArgumentNullException.ThrowIfNull(match);

        for (var end = startIndex + count; startIndex < end;)
        {
            var n = ChunkAt(startIndex, end - startIndex, out var array, out var offset);
            for (var i = offset; i < offset + n; i++)
            {
                if (match(array[i]))
                    return startIndex + i - offset;
            }
            startIndex += n;
        }
        return -1;
    }

    /// <summary>
    /// Returns the index of the last element that matches <paramref name="match"/>, or -1.
    /// </summary>
    /// <param name="match">The predicate to test elements with.</param>
    public int FindLastIndex(Predicate<T> match) => FindLastIndex(_count - 1, _count, match);
    /// <summary>
    /// Returns the index of the last element at or before <paramref name="startIndex"/> that matches <paramref name="match"/>, or -1.
    /// </summary>
    /// <param name="startIndex">The zero-based starting index of the backward search.</param>
    /// <param name="match">The predicate to test elements with.</param>
    public int FindLastIndex(int startIndex, Predicate<T> match) => FindLastIndex(startIndex, startIndex + 1, match);
    /// <summary>
    /// Returns the index of the last element within <paramref name="count"/> elements ending at <paramref name="startIndex"/> that matches <paramref name="match"/>, or -1.
    /// </summary>
    /// <param name="startIndex">The zero-based starting index of the backward search.</param>
    /// <param name="count">The number of elements to search.</param>
    /// <param name="match">The predicate to test elements with.</param>
    public int FindLastIndex(int startIndex, int count, Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (_count == 0)
        {
            if (startIndex != -1)
                ThrowIndexOutOfRange(startIndex);
        }
        else if ((uint)startIndex >= (uint)_count)
            ThrowIndexOutOfRange(startIndex);
        if (count < 0 || startIndex - count + 1 < 0)
            ThrowCountOutOfRange(count);

        for (int end = startIndex + 1, stop = end - count; end > stop;)
        {
            var n = ChunkBefore(end, end - stop, out var array, out var offset);
            for (var i = offset + n - 1; i >= offset; i--)
            {
                if (match(array[i]))
                    return end - n + i - offset;
            }
            end -= n;
        }
        return -1;
    }

    /// <summary>
    /// Searches the sorted list for <paramref name="item"/> using the default comparer.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    /// <returns>The index of <paramref name="item"/> if found; otherwise the bitwise complement of the index of the next larger element, or of <see cref="Count"/>.</returns>
    public int BinarySearch(T item) => BinarySearch(0, _count, item, null);
    /// <summary>
    /// Searches the sorted list for <paramref name="item"/> using <paramref name="comparer"/>.
    /// </summary>
    /// <param name="item">The element to locate.</param>
    /// <param name="comparer">The comparer to use, or <see langword="null"/> for the default comparer.</param>
    /// <returns>The index of <paramref name="item"/> if found; otherwise the bitwise complement of the index of the next larger element, or of <see cref="Count"/>.</returns>
    public int BinarySearch(T item, IComparer<T> comparer) => BinarySearch(0, _count, item, comparer);
    /// <summary>
    /// Searches a sorted range of the list for <paramref name="item"/> using <paramref name="comparer"/>.
    /// </summary>
    /// <param name="index">The zero-based starting index of the range.</param>
    /// <param name="count">The length of the range.</param>
    /// <param name="item">The element to locate.</param>
    /// <param name="comparer">The comparer to use, or <see langword="null"/> for the default comparer.</param>
    /// <returns>The index of <paramref name="item"/> if found; otherwise the bitwise complement of the index of the next larger element, or of the end of the range.</returns>
    public int BinarySearch(int index, int count, T item, IComparer<T> comparer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_count - index < count)
            ThrowInvalidRange();

        comparer ??= Comparer<T>.Default;
        int lo = index, hi = index + count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var segment = Locate(mid, out var offset);
            var c = comparer.Compare(_segments[segment][offset], item);
            if (c == 0)
                return mid;
            if (c < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return ~lo;
    }
    #endregion

    #region Reordering
    /// <summary>
    /// Reverses the order of all elements.
    /// </summary>
    public void Reverse() => Reverse(0, _count);
    /// <summary>
    /// Reverses the order of <paramref name="count"/> elements starting at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">The zero-based starting index of the range.</param>
    /// <param name="count">The length of the range.</param>
    public void Reverse(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_count - index < count)
            ThrowInvalidRange();
        ReverseCore(index, count);
        _version++;
    }

    private void ReverseCore(int index, int count)
    {
        if (count < 2)
            return;

        var ls = Locate(index, out var lo);
        var rs = Locate(index + count - 1, out var ro);
        var la = _segments[ls];
        var ra = _segments[rs];
        var ll = SegmentLength(ls);
        for (var i = count >> 1; i > 0; i--)
        {
            (la[lo], ra[ro]) = (ra[ro], la[lo]);
            if (++lo == ll && i > 1)
            {
                la = _segments[++ls];
                ll = SegmentLength(ls);
                lo = 0;
            }
            if (--ro < 0 && i > 1)
            {
                ra = _segments[--rs];
                ro = SegmentLength(rs) - 1;
            }
        }
    }

    /// <summary>
    /// Sorts all elements using the default comparer.
    /// </summary>
    public void Sort() => Sort(0, _count, null);
    /// <summary>
    /// Sorts all elements using <paramref name="comparer"/>.
    /// </summary>
    /// <param name="comparer">The comparer to use, or <see langword="null"/> for the default comparer.</param>
    public void Sort(IComparer<T> comparer) => Sort(0, _count, comparer);
    /// <summary>
    /// Sorts all elements using <paramref name="comparison"/>.
    /// </summary>
    /// <param name="comparison">The comparison to use.</param>
    public void Sort(Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        Sort(0, _count, Comparer<T>.Create(comparison));
    }
    /// <summary>
    /// Sorts <paramref name="count"/> elements starting at <paramref name="index"/> using <paramref name="comparer"/>.
    /// Ranges spanning multiple segments are sorted in a temporary buffer rented from <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <param name="index">The zero-based starting index of the range.</param>
    /// <param name="count">The length of the range.</param>
    /// <param name="comparer">The comparer to use, or <see langword="null"/> for the default comparer.</param>
    public void Sort(int index, int count, IComparer<T> comparer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_count - index < count)
            ThrowInvalidRange();

        if (count > 1)
        {
            if (ChunkAt(index, count, out var array, out var offset) == count)
            {
                Array.Sort(array, offset, count, comparer);
            }
            else
            {
                var buffer = ArrayPool<T>.Shared.Rent(count);
                try
                {
                    Read(index, buffer.AsSpan(0, count));
                    Array.Sort(buffer, 0, count, comparer);
                    Write(index, buffer.AsSpan(0, count));
                }
                finally
                {
                    buffer.AsSpan(0, count).ZeroMemory();
                    ArrayPool<T>.Shared.Return(buffer);
                }
            }
        }
        _version++;
    }
    #endregion

    #region Copying
    /// <summary>
    /// Copies all elements into a new array.
    /// </summary>
    public T[] ToArray()
    {
        if (_count == 0)
            return [];
#if NETCOREAPP
        var array = GC.AllocateUninitializedArray<T>(_count);
#else
        var array = new T[_count];
#endif
        Read(0, array);
        return array;
    }

    /// <summary>
    /// Returns a new <see cref="SegmentedListBase{T}"/> with the same segment sizes containing <paramref name="count"/> elements starting at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">The zero-based starting index of the range.</param>
    /// <param name="count">The length of the range.</param>
    public SegmentedListBase<T> GetRange(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_count - index < count)
            ThrowInvalidRange();

        var result = EmptyFromThis();
        result.EnsureCapacity(count);
        for (var end = index + count; index < end;)
        {
            var n = ChunkAt(index, end - index, out var array, out var offset);
            result.AddRange(new ReadOnlySpan<T>(array, offset, n));
            index += n;
        }
        return result;
    }

    /// <summary>
    /// Copies all elements into <paramref name="array"/>.
    /// </summary>
    /// <param name="array">The destination array.</param>
    public void CopyTo(T[] array) => CopyTo(array, 0);
    /// <summary>
    /// Copies all elements into <paramref name="array"/> starting at <paramref name="arrayIndex"/>.
    /// </summary>
    /// <param name="array">The destination array.</param>
    /// <param name="arrayIndex">The zero-based index in <paramref name="array"/> at which copying begins.</param>
    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        CopyTo(0, array, arrayIndex, _count);
    }
    /// <summary>
    /// Copies <paramref name="count"/> elements starting at <paramref name="index"/> into <paramref name="array"/> starting at <paramref name="arrayIndex"/>.
    /// </summary>
    /// <param name="index">The zero-based index in the list at which copying begins.</param>
    /// <param name="array">The destination array.</param>
    /// <param name="arrayIndex">The zero-based index in <paramref name="array"/> at which copying begins.</param>
    /// <param name="count">The number of elements to copy.</param>
    public void CopyTo(int index, T[] array, int arrayIndex, int count)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_count - index < count)
            ThrowInvalidRange();
        Read(index, array.AsSpan(arrayIndex, count));
    }
    /// <summary>
    /// Copies all elements into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The destination span. Must be at least <see cref="Count"/> elements long.</param>
    public void CopyTo(Span<T> destination)
    {
        if (destination.Length < _count)
            throw new ArgumentException("Destination is too short.", nameof(destination));
        Read(0, destination.Slice(0, _count));
    }
    #endregion

    #region Enumeration
    /// <summary>
    /// Invokes <paramref name="action"/> on each element.
    /// </summary>
    /// <param name="action">The action to invoke.</param>
    public void ForEach(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var version = _version;
        for (var index = 0; index < _count;)
        {
            var n = ChunkAt(index, _count - index, out var array, out var offset);
            for (var i = offset; i < offset + n; i++)
            {
                action(array[i]);
                if (version != _version)
                    ThrowVersionChanged();
            }
            index += n;
        }
    }

    /// <summary>
    /// Returns an enumerator over the elements of the list.
    /// </summary>
    public Enumerator GetEnumerator() => new Enumerator(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Returns an enumerator over the occupied portion of each segment, in order.
    /// The list must not be modified while the enumeration is in progress.
    /// </summary>
    public SegmentEnumerator EnumerateSegments() => new SegmentEnumerator(this);

    /// <summary>
    /// Enumerates the elements of a <see cref="SegmentedListBase{T}"/>.
    /// </summary>
    public struct Enumerator : IEnumerator<T>
    {
        private readonly SegmentedListBase<T> _list;
        private readonly int _version;
        private T[] _segment;
        private int _segmentIndex;
        private int _offset;
        private int _length;
        private int _remaining;
        private T _current;

        internal Enumerator(SegmentedListBase<T> list)
        {
            _list = list;
            _version = list._version;
            _segmentIndex = -1;
            _remaining = list._count;
        }

        /// <inheritdoc/>
        public readonly T Current => _current;
        readonly object IEnumerator.Current => _current;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            var offset = _offset;
            if (offset < _length && _version == _list._version)
            {
                _current = At(_segment, offset);
                _offset = offset + 1;
                return true;
            }
            return MoveNextSegment();
        }

        private bool MoveNextSegment()
        {
            if (_version != _list._version)
                ThrowVersionChanged();
            if (_remaining == 0)
            {
                _current = default;
                return false;
            }

            var segment = ++_segmentIndex;
            _segment = _list._segments[segment];
            _length = Math.Min(_list.SegmentLength(segment), _remaining);
            _remaining -= _length;
            _current = _segment[0];
            _offset = 1;
            return true;
        }

        /// <inheritdoc/>
        public void Reset()
        {
            if (_version != _list._version)
                ThrowVersionChanged();
            this = new Enumerator(_list);
        }

        /// <inheritdoc/>
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Enumerates the occupied portion of each segment of a <see cref="SegmentedListBase{T}"/>.
    /// </summary>
    public ref struct SegmentEnumerator
    {
        private readonly SegmentedListBase<T> _list;
        private int _segment;
        private int _remaining;
        private Span<T> _current;

        internal SegmentEnumerator(SegmentedListBase<T> list)
        {
            _list = list;
            _segment = -1;
            _remaining = list._count;
        }

        /// <summary>
        /// Gets the occupied portion of the current segment.
        /// </summary>
        public readonly Span<T> Current => _current;

        /// <summary>
        /// Returns this enumerator.
        /// </summary>
        public readonly SegmentEnumerator GetEnumerator() => this;

        /// <summary>
        /// Advances to the next segment that holds elements.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator advanced; <see langword="false"/> if no segments remain.</returns>
        public bool MoveNext()
        {
            if (_remaining == 0)
                return false;
            var segment = ++_segment;
            var length = Math.Min(_list.SegmentLength(segment), _remaining);
            _current = _list._segments[segment].AsSpan(0, length);
            _remaining -= length;
            return true;
        }
    }
    #endregion

    #region Explicit interface implementations
    T IList<T>.this[int index]
    {
        get => this[index];
        set => this[index] = value;
    }
    T IReadOnlyList<T>.this[int index] => this[index];
    bool ICollection<T>.IsReadOnly => false;
    #endregion

    #region Throw helpers
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndexOutOfRange(int index) => throw new ArgumentOutOfRangeException(nameof(index), index, "Index was out of range.");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCountOutOfRange(int count) => throw new ArgumentOutOfRangeException(nameof(count), count, "Count was out of range.");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidRange() => throw new ArgumentException("Index and count do not denote a valid range of elements.");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCapacityExceeded() => throw new InvalidOperationException("The list cannot hold more than int.MaxValue elements.");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowVersionChanged() => throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
    #endregion
}

/// <summary>
/// A <see cref="SegmentedListBase{T}"/> whose segments are allocated on demand.
/// </summary>
/// <typeparam name="T">The type of elements in the list.</typeparam>
public sealed class SegmentedList<T> : SegmentedListBase<T>
{
    /// <summary>
    /// Initializes a new, empty <see cref="SegmentedList{T}"/> with the default segment sizes.
    /// </summary>
    public SegmentedList() : base() { }
    /// <summary>
    /// Initializes a new <see cref="SegmentedList{T}"/> with the default segment sizes and room for at least <paramref name="capacity"/> elements.
    /// </summary>
    /// <param name="capacity">The minimum number of elements the list can hold without allocating.</param>
    public SegmentedList(int capacity) : base(capacity) { }
    /// <summary>
    /// Initializes a new <see cref="SegmentedList{T}"/> with the default segment sizes containing the elements of <paramref name="collection"/>.
    /// </summary>
    /// <param name="collection">The collection whose elements are copied into the list.</param>
    public SegmentedList(IEnumerable<T> collection) : base(collection) { }
    /// <summary>
    /// Initializes a new <see cref="SegmentedList{T}"/> with the specified segment sizes.
    /// </summary>
    /// <param name="minSegmentSize">The size of the first segment. Rounded up to a power of two.</param>
    /// <param name="maxSegmentSize">The size segments stop growing at. Rounded up to a power of two; must not exceed 2^30. Equal to <paramref name="minSegmentSize"/> for fixed-size segments.</param>
    /// <param name="capacity">The minimum number of elements the list can hold without allocating.</param>
    public SegmentedList(int minSegmentSize, int maxSegmentSize, int capacity = 0) : base(minSegmentSize, maxSegmentSize, capacity) { }

    /// <inheritdoc/>
    protected override SegmentedListBase<T> EmptyFromThis() => new SegmentedList<T>(MinSegmentSize, MaxSegmentSize);
}

/// <summary>
/// A <see cref="SegmentedListBase{T}"/> whose segments are rented from an <see cref="ArrayPool{T}"/> and returned to it when released.
/// </summary>
/// <remarks>
/// Segments are returned by <see cref="SegmentedListBase{T}.TrimExcess"/> and <see cref="Dispose"/>. Arrays are cleared on return if <typeparamref name="T"/> is or contains references, or if requested at construction.
/// Disposing returns every segment and leaves the instance as a usable empty list.
/// </remarks>
/// <typeparam name="T">The type of elements in the list.</typeparam>
public sealed class PooledSegmentedList<T> : SegmentedListBase<T>, IDisposable
{
    private readonly ArrayPool<T> _pool;
    private readonly bool _clearOnReturn;

    /// <summary>
    /// Initializes a new, empty <see cref="PooledSegmentedList{T}"/> with the default segment sizes, renting from <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public PooledSegmentedList() : this((ArrayPool<T>)null) { }
    /// <summary>
    /// Initializes a new, empty <see cref="PooledSegmentedList{T}"/> with the default segment sizes.
    /// </summary>
    /// <param name="pool">The pool to rent segments from, or <see langword="null"/> for <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="clearOnReturn">Whether to clear segments on return to the pool even if <typeparamref name="T"/> holds no references.</param>
    public PooledSegmentedList(ArrayPool<T> pool, bool clearOnReturn = false)
    {
        _pool = pool ?? ArrayPool<T>.Shared;
        _clearOnReturn = clearOnReturn;
    }
    /// <summary>
    /// Initializes a new <see cref="PooledSegmentedList{T}"/> with the default segment sizes and room for at least <paramref name="capacity"/> elements.
    /// </summary>
    /// <param name="capacity">The minimum number of elements the list can hold without renting.</param>
    /// <param name="pool">The pool to rent segments from, or <see langword="null"/> for <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="clearOnReturn">Whether to clear segments on return to the pool even if <typeparamref name="T"/> holds no references.</param>
    public PooledSegmentedList(int capacity, ArrayPool<T> pool = null, bool clearOnReturn = false) : this(pool, clearOnReturn) => EnsureCapacity(capacity);
    /// <summary>
    /// Initializes a new <see cref="PooledSegmentedList{T}"/> with the default segment sizes containing the elements of <paramref name="collection"/>.
    /// </summary>
    /// <param name="collection">The collection whose elements are copied into the list.</param>
    /// <param name="pool">The pool to rent segments from, or <see langword="null"/> for <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="clearOnReturn">Whether to clear segments on return to the pool even if <typeparamref name="T"/> holds no references.</param>
    public PooledSegmentedList(IEnumerable<T> collection, ArrayPool<T> pool = null, bool clearOnReturn = false) : this(pool, clearOnReturn) => AddRange(collection);
    /// <summary>
    /// Initializes a new <see cref="PooledSegmentedList{T}"/> with the specified segment sizes.
    /// </summary>
    /// <param name="minSegmentSize">The size of the first segment. Rounded up to a power of two.</param>
    /// <param name="maxSegmentSize">The size segments stop growing at. Rounded up to a power of two; must not exceed 2^30. Equal to <paramref name="minSegmentSize"/> for fixed-size segments.</param>
    /// <param name="capacity">The minimum number of elements the list can hold without renting.</param>
    /// <param name="pool">The pool to rent segments from, or <see langword="null"/> for <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="clearOnReturn">Whether to clear segments on return to the pool even if <typeparamref name="T"/> holds no references.</param>
    public PooledSegmentedList(int minSegmentSize, int maxSegmentSize, int capacity = 0, ArrayPool<T> pool = null, bool clearOnReturn = false) : base(minSegmentSize, maxSegmentSize)
    {
        _pool = pool ?? ArrayPool<T>.Shared;
        _clearOnReturn = clearOnReturn;
        EnsureCapacity(capacity);
    }

    /// <inheritdoc/>
    protected override T[] AllocateSegment(int length) => _pool.Rent(length);
    /// <inheritdoc/>
    protected override void ReleaseSegment(T[] segment) => _pool.ReturnSafe(segment, _clearOnReturn);
    /// <inheritdoc/>
    protected override SegmentedListBase<T> EmptyFromThis() => new SegmentedList<T>(MinSegmentSize, MaxSegmentSize);

    /// <summary>
    /// Returns all segments to the pool, leaving the list empty.
    /// </summary>
    public void Dispose() => ReleaseAll();
}
