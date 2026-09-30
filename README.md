# `LaquaiLib.Buffers`

All things pooling and buffer primitives.

- **`ArrayPoolMemoryStream`**: a `Stream` (and `IBufferWriter<byte>`) whose backing memory is rented from an `ArrayPool<byte>` in segments instead of held as one contiguous, GC-owned array like `MemoryStream`. Growth rents another segment rather than copying everything into a bigger array. Not meant to replace something like [`Microsoft.IO.RecyclableMemoryStream`](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) since this is a much simpler, single-file implementation for when you don't want that dependency.
- **`SegmentedList<T>`** / **`PooledSegmentedList<T>`** (`LaquaiLib.Buffers.Collections`): `List<T>`-like lists stored across a chain of arrays. Segments start small and double up to a configurable maximum (by default the largest size that stays off the LOH), so growth never copies or moves existing elements and indexing stays O(1). Pass equal min/max segment sizes for fixed-size segments. The pooled variant rents its segments from an `ArrayPool<T>` and returns them on `TrimExcess`/`Dispose`. Both derive from the closed `SegmentedListBase<T>`, which holds the entire list API, so it can be used as a common parameter or field type for either; it cannot be derived from outside the library.
- **`RentedArray<T>`** (`LaquaiLib.Buffers.Wrappers`): a disposable wrapper around an array rented from an `ArrayPool<T>`, an offset/length view over it, and the pool it came from, so returning it is a `using` away instead of a manual `try`/`finally`.
- **Extensions** (`LaquaiLib.Buffers.Extensions`):
  - `ArrayPool<T>.ReturnSafe(array)`: returns an array (ignoring `null`), clearing it only if `T` is or contains references.
  - `ArrayPool<TSource>.Rent<TAs>(minimumSize, out Span<TAs> span)`: rents an unmanaged array sized for `minimumSize` elements of another unmanaged type and hands out a reinterpreted span over it.
  - `Span<T>.ZeroMemory()`: clears a span in a way the JIT won't elide, for wiping sensitive data before it goes out of scope or back to a pool.

Targets `netstandard2.0`/`net10.0`/`net11.0`.

Contact me on Discord @ `eyeoftheenemy` or open an issue here if you have any questions, suggestions or want to contribute!
