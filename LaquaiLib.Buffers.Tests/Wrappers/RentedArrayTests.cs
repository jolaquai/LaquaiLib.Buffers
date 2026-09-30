using System.Buffers;

using LaquaiLib.Buffers.Wrappers;

namespace LaquaiLib.Buffers.Tests.Wrappers;

public class RentedArrayTests
{
    private struct StructWithReference
    {
        public int A;
        public string S;
    }

    private sealed class RecordingArrayPool<T> : ArrayPool<T>
    {
        public T[] LastReturnedArray;
        public bool? LastReturnedClearArray;
        public bool ReturnWasCalled;

        public override T[] Rent(int minimumLength) => new T[minimumLength];

        public override void Return(T[] array, bool clearArray = false)
        {
            ReturnWasCalled = true;
            LastReturnedArray = array;
            LastReturnedClearArray = clearArray;
        }
    }

    [Fact]
    public void SpanCoversOffsetAndLengthOfTheArray()
    {
        var array = new[] { 0, 1, 2, 3, 4, 5 };
        var rented = new RentedArray<int>(array, 2, 3, new RecordingArrayPool<int>());
        Assert.Equal([2, 3, 4], rented.Span.ToArray());
        rented.Span[0] = 42;
        Assert.Equal(42, array[2]);
        Assert.Equal([1, 42, 3, 4, 5], new RentedArray<int>(array, 1, arrayPool: new RecordingArrayPool<int>()).Span.ToArray());
    }

    [Fact]
    public void DisposeClearsReferenceTypeArrayEvenWhenClearIsFalse()
    {
        var pool = new RecordingArrayPool<string>();
        var rented = new RentedArray<string>(new string[4], arrayPool: pool, clear: false);

        rented.Dispose();

        Assert.True(pool.ReturnWasCalled);
        Assert.True(pool.LastReturnedClearArray);
    }

    [Fact]
    public void DisposeClearsStructContainingReferenceEvenWhenClearIsFalse()
    {
        var pool = new RecordingArrayPool<StructWithReference>();
        var rented = new RentedArray<StructWithReference>(new StructWithReference[4], arrayPool: pool, clear: false);

        rented.Dispose();

        Assert.True(pool.ReturnWasCalled);
        Assert.True(pool.LastReturnedClearArray);
    }

    [Fact]
    public void DisposeDoesNotClearPureValueTypeArrayWhenClearIsFalse()
    {
        var pool = new RecordingArrayPool<int>();
        var rented = new RentedArray<int>(new int[4], arrayPool: pool, clear: false);

        rented.Dispose();

        Assert.True(pool.ReturnWasCalled);
        Assert.False(pool.LastReturnedClearArray);
    }

    [Fact]
    public void DisposeClearsPureValueTypeArrayWhenClearIsTrue()
    {
        var pool = new RecordingArrayPool<int>();
        var rented = new RentedArray<int>(new int[4], arrayPool: pool, clear: true);

        rented.Dispose();

        Assert.True(pool.ReturnWasCalled);
        Assert.True(pool.LastReturnedClearArray);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var pool = new RecordingArrayPool<int>();
        var rented = new RentedArray<int>(new int[4], arrayPool: pool);

        rented.Dispose();
        pool.ReturnWasCalled = false;
        rented.Dispose();

        Assert.False(pool.ReturnWasCalled);
    }

    [Fact]
    public void DisposeUsesSharedPoolByDefault()
    {
        var rented = new RentedArray<byte>(ArrayPool<byte>.Shared.Rent(16));
        rented.Dispose();
    }
}
