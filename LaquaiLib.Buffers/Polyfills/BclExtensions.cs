namespace LaquaiLib.Buffers;

#if !NETCOREAPP
using System.Collections.Concurrent;
using System.Reflection;

internal static class ExceptionExtensions
{
    extension(ObjectDisposedException)
    {
        #region ObjectDisposedException throw helpers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIf(bool disposed, object obj) => ThrowIf(disposed, obj?.GetType());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIf(bool disposed, Type type) => ThrowIf(disposed, type?.FullName);
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowIf(bool disposed, string description)
        {
            if (disposed)
                throw new ObjectDisposedException(description, "The instance has been disposed.");
        }
        #endregion
    }
    extension(ArgumentOutOfRangeException)
    {
        #region ArgumentOutOfRangeException throw helpers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfGreaterThan<T>(T value, T maxValue, [CallerArgumentExpression(nameof(value))] string paramName = "") where T : IComparable<T>
        {
            if (value.CompareTo(maxValue) > 0)
                ThrowGreaterThan(paramName, value, maxValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfGreaterThanOrEqualTo<T>(T value, T maxValue, [CallerArgumentExpression(nameof(value))] string paramName = "") where T : IComparable<T>
        {
            if (value.CompareTo(maxValue) >= 0)
                ThrowGreaterThanOrEqualTo(paramName, value, maxValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfLessThan<T>(T value, T minValue, [CallerArgumentExpression(nameof(value))] string paramName = "") where T : IComparable<T>
        {
            if (value.CompareTo(minValue) < 0)
                ThrowLessThan(paramName, value, minValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfLessThanOrEqualTo<T>(T value, T minValue, [CallerArgumentExpression(nameof(value))] string paramName = "") where T : IComparable<T>
        {
            if (value.CompareTo(minValue) <= 0)
                ThrowLessThanOrEqualTo(paramName, value, minValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfNegative<T>(T value, [CallerArgumentExpression(nameof(value))] string paramName = "") where T : IComparable<T>
        {
            if (value.CompareTo(default) < 0)
                ThrowNegative(paramName, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfNegativeOrZero<T>(T value, [CallerArgumentExpression(nameof(value))] string paramName = "") where T : IComparable<T>
        {
            if (value.CompareTo(default) <= 0)
                ThrowNegativeOrZero(paramName, value);
        }
        #endregion
    }
    #region Outlined throw helpers
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowGreaterThan<T>(string paramName, T value, T maxValue) =>
        throw new ArgumentOutOfRangeException(paramName, value, $"The value must be less than or equal to {maxValue}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowGreaterThanOrEqualTo<T>(string paramName, T value, T maxValue) =>
        throw new ArgumentOutOfRangeException(paramName, value, $"The value must be less than {maxValue}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLessThan<T>(string paramName, T value, T minValue) =>
        throw new ArgumentOutOfRangeException(paramName, value, $"The value must be greater than or equal to {minValue}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLessThanOrEqualTo<T>(string paramName, T value, T minValue) =>
        throw new ArgumentOutOfRangeException(paramName, value, $"The value must be greater than {minValue}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNegative<T>(string paramName, T value) =>
        throw new ArgumentOutOfRangeException(paramName, value, "The value must be non-negative.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNegativeOrZero<T>(string paramName, T value) =>
        throw new ArgumentOutOfRangeException(paramName, value, "The value must be positive.");
    #endregion
    extension(ArgumentNullException)
    {
        public static void ThrowIfNull<T>(T obj, [CallerArgumentExpression(nameof(obj))] string paramName = "")
        {
            if (obj == null)
                throw new ArgumentNullException(paramName);
        }
    }
}
internal static class ArrayExtensions
{
    extension(Array)
    {
        public static int MaxLength => 0x7FFFFFC7;
    }
}
internal static class StreamExtensions
{
    extension(Stream stream)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateBufferArguments(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
            if ((uint)count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count), count, "The count is invalid.");
        }
    }
}
internal static class RuntimeHelpersExtensions
{
    private static class RefCheck<T>
    {
        public static readonly bool Value = ComputeHasRefs(typeof(T));
    }
    private static readonly Func<Type, bool> _computeHasRefs = ComputeHasRefs;
    private static readonly ConcurrentDictionary<Type, bool> _byType = [];
    private static bool ComputeHasRefs(Type type)
    {
        if (!type.IsValueType)
            return true;
        if (type.IsPrimitive || type.IsEnum || type.IsPointer)
            return false;

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return ComputeHasRefs(underlying);

        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (ComputeHasRefs(f.FieldType))
                return true;
        }
        return false;
    }

    extension(RuntimeHelpers)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsReferenceOrContainsReferences<T>() => RefCheck<T>.Value;
        public static bool IsReferenceOrContainsReferences(Type type) => _byType.GetOrAdd(type ?? throw new ArgumentNullException(nameof(type)), _computeHasRefs);
    }
}
internal static class CollectionsMarshal
{
    public static Span<T> AsSpan<T>(List<T> list)
    {
        if (list == null)
            throw new ArgumentNullException(nameof(list));
        return ListAccessors<T>._items(list).AsSpan(0, list.Count);
    }
}
#endif