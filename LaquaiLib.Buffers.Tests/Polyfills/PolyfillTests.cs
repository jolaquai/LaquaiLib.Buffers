#if NETFRAMEWORK
namespace LaquaiLib.Buffers.Tests.PolyfillTests;

public class TaskToAsyncResultTests
{
    [Fact]
    public void IncompleteTaskInvokesCallbackOnCompletion()
    {
        var tcs = new TaskCompletionSource<int>();
        using var called = new ManualResetEventSlim();
        IAsyncResult seen = null;
        var state = new object();
        var result = TaskToAsyncResult.Begin(tcs.Task, ar => { seen = ar; called.Set(); }, state);

        Assert.False(result.IsCompleted);
        Assert.False(result.CompletedSynchronously);
        Assert.Same(state, result.AsyncState);
        Assert.NotNull(result.AsyncWaitHandle);
        Assert.False(called.IsSet);

        tcs.SetResult(7);
        Assert.True(called.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(result, seen);
        Assert.True(result.IsCompleted);
        Assert.Equal(7, TaskToAsyncResult.End<int>(result));
        Assert.Same(tcs.Task, TaskToAsyncResult.Unwrap(result));
    }

    [Fact]
    public void CompletedTaskCompletesSynchronously()
    {
        var invoked = false;
        var result = TaskToAsyncResult.Begin(Task.CompletedTask, _ => invoked = true, null);
        Assert.True(result.CompletedSynchronously);
        Assert.True(invoked);
        TaskToAsyncResult.End(result);
    }

    [Fact]
    public void BeginRejectsNullTask() => Assert.Throws<ArgumentNullException>(() => TaskToAsyncResult.Begin(null, null, null));

    private sealed class Foreign : IAsyncResult
    {
        public object AsyncState => null;
        public WaitHandle AsyncWaitHandle => null;
        public bool CompletedSynchronously => true;
        public bool IsCompleted => true;
    }

    [Fact]
    public void UnwrapAndEndRejectForeignAndMismatchedResults()
    {
        var foreign = new Foreign();
        Assert.Throws<ArgumentNullException>(() => { _ = TaskToAsyncResult.Unwrap(null); });
        Assert.Throws<ArgumentNullException>(() => { _ = TaskToAsyncResult.Unwrap<int>(null); });
        Assert.Throws<ArgumentException>(() => { _ = TaskToAsyncResult.Unwrap(foreign); });
        Assert.Throws<ArgumentException>(() => { _ = TaskToAsyncResult.Unwrap<int>(foreign); });
        Assert.Throws<ArgumentException>(() => { TaskToAsyncResult.End(foreign); });
        Assert.Throws<ArgumentException>(() => { _ = TaskToAsyncResult.End<int>(foreign); });
        var plain = TaskToAsyncResult.Begin(Task.CompletedTask, null, null);
        Assert.Throws<ArgumentException>(() => { _ = TaskToAsyncResult.Unwrap<int>(plain); });
    }
}

public class BclExtensionsTests
{
    [Fact]
    public void ObjectDisposedThrowIfHonoursFlag()
    {
        ObjectDisposedException.ThrowIf(false, new object());
        ObjectDisposedException.ThrowIf(false, (object)null);
        ObjectDisposedException.ThrowIf(false, (Type)null);
        ObjectDisposedException.ThrowIf(false, (string)null);

        var ex = Assert.Throws<ObjectDisposedException>(() => ObjectDisposedException.ThrowIf(true, new object()));
        Assert.Equal(typeof(object).FullName, ex.ObjectName);
        Assert.Empty(Assert.Throws<ObjectDisposedException>(() => ObjectDisposedException.ThrowIf(true, (object)null)).ObjectName);
        Assert.Equal("x", Assert.Throws<ObjectDisposedException>(() => ObjectDisposedException.ThrowIf(true, "x")).ObjectName);
    }

    [Fact]
    public void ThrowIfNegativeBranches()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(0);
        ArgumentOutOfRangeException.ThrowIfNegative(1L);
        Assert.Throws<ArgumentOutOfRangeException>(() => ArgumentOutOfRangeException.ThrowIfNegative(-1));
    }

    [Fact]
    public void ThrowIfNegativeOrZeroBranches()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(-1));
    }

    [Fact]
    public void ThrowIfGreaterAndLessThanBranches()
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(1, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => ArgumentOutOfRangeException.ThrowIfGreaterThan(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArgumentOutOfRangeException.ThrowIfLessThan(0, 1));
    }

    [Fact]
    public void ThrowIfNullBranches()
    {
        ArgumentNullException.ThrowIfNull(new object());
        Assert.Throws<ArgumentNullException>(() => ArgumentNullException.ThrowIfNull((object)null));
    }
}
#endif
