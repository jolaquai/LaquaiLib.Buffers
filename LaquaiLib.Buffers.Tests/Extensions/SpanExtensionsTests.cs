using LaquaiLib.Buffers.Extensions;

namespace LaquaiLib.Buffers.Tests.Extensions;

public class SpanExtensionsTests
{
    [Fact]
    public void ZeroMemoryClearsEveryElement()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        Span<byte> span = data;
        span.ZeroMemory();
        Assert.All(data, static b => Assert.Equal(0, b));
    }

    [Fact]
    public void ZeroMemoryOnlyAffectsTheSlice()
    {
        var data = new int[] { 1, 2, 3, 4, 5 };
        Span<int> slice = data.AsSpan(1, 3);
        slice.ZeroMemory();
        Assert.Equal([1, 0, 0, 0, 5], data);
    }

    [Fact]
    public void ZeroMemoryNullsReferences()
    {
        var data = new[] { "a", "b" };
        Span<string> span = data;
        span.ZeroMemory();
        Assert.All(data, static s => Assert.Null(s));
    }

    [Fact]
    public void ZeroMemoryAcceptsEmptySpan()
    {
        Span<int> span = default;
        span.ZeroMemory();
        Assert.True(span.IsEmpty);
    }
}
