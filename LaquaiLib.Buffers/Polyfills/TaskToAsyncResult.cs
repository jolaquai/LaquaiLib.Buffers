#if !NETCOREAPP
namespace LaquaiLib.Buffers;

internal static class TaskToAsyncResult
{
    private sealed class TaskAsyncResult : IAsyncResult
    {
        internal readonly Task _task;
        private readonly AsyncCallback _callback;

        public object AsyncState { get; }
        public bool CompletedSynchronously { get; }

        public bool IsCompleted => _task.IsCompleted;

        public WaitHandle AsyncWaitHandle => ((IAsyncResult)_task).AsyncWaitHandle;

        internal TaskAsyncResult(Task task, object state, AsyncCallback callback)
        {
            _task = task;
            AsyncState = state;
            if (task.IsCompleted)
            {
                CompletedSynchronously = true;
                callback?.Invoke(this);
            }
            else if (callback != null)
            {
                _callback = callback;
                _task.ConfigureAwait(continueOnCapturedContext: false).GetAwaiter().OnCompleted(delegate
                {
                    _callback(this);
                });
            }
        }
    }

    public static IAsyncResult Begin(Task task, AsyncCallback callback, object state)
    {
        ArgumentNullException.ThrowIfNull(task, "task");
        return new TaskAsyncResult(task, state, callback);
    }
    public static void End(IAsyncResult asyncResult)
    {
        Unwrap(asyncResult).GetAwaiter().GetResult();
    }
    public static TResult End<TResult>(IAsyncResult asyncResult)
    {
        return Unwrap<TResult>(asyncResult).GetAwaiter().GetResult();
    }
    public static Task Unwrap(IAsyncResult asyncResult)
    {
        ArgumentNullException.ThrowIfNull(asyncResult, "asyncResult");
        return (asyncResult as TaskAsyncResult)?._task ?? throw new ArgumentException(null, "asyncResult");
    }
    public static Task<TResult> Unwrap<TResult>(IAsyncResult asyncResult)
    {
        ArgumentNullException.ThrowIfNull(asyncResult, "asyncResult");
        return ((asyncResult as TaskAsyncResult)?._task as Task<TResult>) ?? throw new ArgumentException(null, "asyncResult");
    }
}
#endif