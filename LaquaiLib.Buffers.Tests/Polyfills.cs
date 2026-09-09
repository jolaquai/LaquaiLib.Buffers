namespace LaquaiLib.Buffers.Tests;

#if !NETCOREAPP
internal static class Polyfills
{
    extension(Array)
    {
        public static void Clear<T>(T[] array)
        {
            for (var i = 0; i < array.Length; i++)
                array[i] = default;
        }
        public static void Fill<T>(T[] array, T value)
        {
            for (var i = 0; i < array.Length; i++)
                array[i] = value;
        }
    }

    // I know this is stupid
    extension(Task t)
    {
        public Task AsTask() => t;
    }
    extension<T>(Task<T> t)
    {
        public Task<T> AsTask() => t;
    }

    extension(Random random)
    {
        public long NextInt64() => random.NextInt64(0, long.MaxValue);
        public long NextInt64(long max) => random.NextInt64(0, max);
        public long NextInt64(long min, long max)
        {
            if (min > max)
                throw new ArgumentOutOfRangeException(nameof(min));
            var range = (ulong)max - (ulong)min;
            if (range == 0)
                return min;
            var limit = ulong.MaxValue - ulong.MaxValue % range;
            var buf = new byte[8];
            ulong v;
            do
            {
                random.NextBytes(buf);
                v = BitConverter.ToUInt64(buf, 0);
            } while (v >= limit);
            return unchecked(min + (long)(v % range));
        }
    }
}
#endif