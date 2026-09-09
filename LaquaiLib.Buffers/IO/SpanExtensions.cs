namespace LaquaiLib.IO;

internal static class SpanExtensions
{
    extension<T>(in Span<T> span)
    {
        // NoInlining|NoOptimization: without it, the JIT is free to prove the write dead and elide it, which defeats the entire point of a "zero this out" call.
        /// <summary>
        /// Generalizes <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory(Span{byte})"/> to arbitrary <see cref="Span{T}"/>s of <typeparamref name="T"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void ZeroMemory() => span.Clear();
    }
}
