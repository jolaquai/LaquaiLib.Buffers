using System.Buffers;

using LaquaiLib.Wrappers;

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
