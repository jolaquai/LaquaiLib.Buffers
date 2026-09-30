using LaquaiLib.Buffers.Internal;

namespace LaquaiLib.Buffers.Tests.Internal;

public class SegmentedBufferHelpersTests
{
    // each segment addresses `addressed` of an array twice that size, so a helper that walked the arrays instead of the addressed extent would map every index past the first segment wrongly
    private static BufferSegment<byte>[] Addressed(params int[] addressed)
    {
        var segments = new BufferSegment<byte>[addressed.Length];
        for (var i = 0; i < addressed.Length; i++)
            segments[i] = new BufferSegment<byte>(new byte[(addressed[i] * 2) + 8], addressed[i]);
        return segments;
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 0, 3)]
    [InlineData(4, 1, 0)]
    [InlineData(9, 1, 5)]
    [InlineData(10, 2, 0)]
    [InlineData(19, 2, 9)]
    public void AbsoluteToRelativeOverSegmentsUsesTheAddressedExtent(long index, int expectedSegment, int expectedOffset)
    {
        var (segment, offset) = SegmentedBufferHelpers.AbsoluteToRelative<byte>(Addressed(4, 6, 10), index);
        Assert.Equal(expectedSegment, segment);
        Assert.Equal(expectedOffset, offset);
    }

    [Fact]
    public void AbsoluteToRelativeOverSegmentsThrowsPastTheAddressedTotal()
    {
        var segments = Addressed(4, 6, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentedBufferHelpers.AbsoluteToRelative<byte>(segments, 20));
    }

    [Fact]
    public void AbsoluteToRelativeOverSegmentsSkipsSegmentsAddressingNothing()
    {
        var (segment, offset) = SegmentedBufferHelpers.AbsoluteToRelative<byte>(Addressed(0, 3), 1);
        Assert.Equal(1, segment);
        Assert.Equal(1, offset);
    }

    [Theory]
    [InlineData(0, 0, 0L)]
    [InlineData(1, 0, 4L)]
    [InlineData(2, 5, 15L)]
    public void RelativeToAbsoluteOverSegmentsSumsAddressedExtents(int segment, int index, long expected)
        => Assert.Equal(expected, SegmentedBufferHelpers.RelativeToAbsolute<byte>(Addressed(4, 6, 10), segment, index));

    [Fact]
    public void ConversionsOverSegmentsRoundTripAcrossEveryValidIndex()
    {
        var segments = Addressed(1, 7, 2, 13, 1);
        for (var i = 0L; i < 24; i++)
        {
            var (segment, offset) = SegmentedBufferHelpers.AbsoluteToRelative<byte>(segments, i);
            Assert.InRange(offset, 0, segments[segment].Length - 1);
            Assert.Equal(i, SegmentedBufferHelpers.RelativeToAbsolute<byte>(segments, segment, offset));
        }
    }
}
