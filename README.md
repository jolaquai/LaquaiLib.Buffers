# `LaquaiLib.Buffers`

All things pooling and buffer primitives.

- **`ArrayPoolMemoryStream`**: a `Stream` (and `IBufferWriter<byte>`) whose backing memory is rented from an `ArrayPool<byte>` in segments instead of held as one contiguous, GC-owned array like `MemoryStream`. Growth rents another segment rather than copying everything into a bigger array. Not meant to replace something like [`Microsoft.IO.RecyclableMemoryStream`](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) since this is a much simpler, single-file implementation for when you don't want that dependency.
- **`RentedArray<T>`**: a disposable wrapper around an array rented from an `ArrayPool<T>`, an offset/length view over it, and the pool it came from, so returning it is a `using` away instead of a manual `try`/`finally`.

`BufferSegment<T>` and `SegmentedBufferHelpers` are internal plumbing shared between the above; not part of the public surface.

Targets `netstandard2.0`/`net10.0`.

Contact me on Discord @ `eyeoftheenemy` or open an issue here if you have any questions, suggestions or want to contribute!
