using System.Buffers;

namespace LaquaiLib.Buffers.Extensions;

/// <summary>
/// Provides extensions for <see cref="ArrayPool{T}"/>.
/// </summary>
public static class ArrayPoolExtensions
{
    extension<T>(ArrayPool<T> pool)
    {
        /// <summary>
        /// Returns <paramref name="array"/> to <paramref name="pool"/>, clearing it first if <paramref name="clearArray"/> is <see langword="true"/> or <typeparamref name="T"/> is a reference type or contains references.
        /// Does nothing if <paramref name="array"/> is <see langword="null"/>.
        /// </summary>
        /// <param name="array">The array to return to the pool.</param>
        /// <param name="clearArray">Whether to clear the array even if <typeparamref name="T"/> holds no references.</param>
        public void ReturnSafe(T[] array, bool clearArray = false)
        {
            if (array is null)
                return;

            if (clearArray || RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                array.AsSpan().ZeroMemory();
            pool.Return(array);
        }
    }

    extension<TSource>(ArrayPool<TSource> pool) where TSource : unmanaged
    {
        /// <summary>
        /// Requests a <typeparamref name="TSource"/> array from the <paramref name="pool"/> and hands out a <typeparamref name="TAs"/>-typed <see cref="Span{T}"/> over it.
        /// <paramref name="minimumSize"/> is used as a base for calculating the minimum number of <typeparamref name="TSource"/> instances to request from the pool, based on the size of <typeparamref name="TAs"/>.
        /// </summary>
        /// <typeparam name="TAs">The <see langword="unmanaged"/> type to cast views over the rented <typeparamref name="TSource"/> array to.</typeparam>
        /// <param name="minimumSize">The minimum number of instances of <typeparamref name="TAs"/> to request for the rented array.</param>
        /// <param name="span">The <typeparamref name="TAs"/>-typed <see cref="Span{T}"/> view over the rented <typeparamref name="TSource"/> array.</param>
        /// <returns>The rented <typeparamref name="TSource"/> array.</returns>
        /// <remarks>
        /// Easiest way to use this method is to call it by explicitly typing the <paramref name="span"/> parameter:
        /// <code lang="csharp">
        /// var array = ArrayPool&lt;byte&gt;.Shared.Rent(minimumSize, out Span&lt;long&gt; span);
        /// // array.GetType() == typeof(byte[])
        /// </code>
        /// </remarks>
        public unsafe TSource[] Rent<TAs>(int minimumSize, out Span<TAs> span) where TAs : unmanaged
        {
            if (minimumSize < 0)
                throw new ArgumentOutOfRangeException(nameof(minimumSize), "The requested size must be non-negative.");

            var effectiveSizeBytes = (long)sizeof(TAs) * minimumSize;
            var effectiveSize = (effectiveSizeBytes + sizeof(TSource) - 1) / sizeof(TSource);
            if (effectiveSize > Array.MaxLength)
                throw new ArgumentOutOfRangeException(nameof(minimumSize), "The requested array size exceeds the maximum allowed length.");

            var arr = pool.Rent((int)effectiveSize);
            if (arr is null || arr.Length < effectiveSize)
                throw new InvalidOperationException("The pool returned an array shorter than requested.");
            var usable = (int)Math.Min(arr.Length, (long)int.MaxValue * sizeof(TAs) / sizeof(TSource));
            span = MemoryMarshal.Cast<TSource, TAs>(arr.AsSpan(0, usable));
            return arr;
        }
    }
}
