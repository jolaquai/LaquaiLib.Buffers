namespace LaquaiLib.IO;

/// <summary>
/// Provides extensions for <see cref="Span{T}"/>.
/// </summary>
public static class SpanExtensions
{
    extension<T>(in Span<T> span)
    {
        /// <summary>
        /// Clears the contents of the <paramref name="span"/>, preventing elision of the store by the JIT.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void ZeroMemory() => span.Clear();
    }
}
