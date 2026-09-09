# `LaquaiLib.Buffers`

Pooled buffer primitives that don't drag in anything else - just the BCL.

- **`ArrayPoolMemoryStream`**: a `Stream` (and `IBufferWriter<byte>`) whose backing memory is rented from an `ArrayPool<byte>` in segments instead of held as one contiguous, GC-owned array like `MemoryStream`. Growth rents another segment rather than copying everything into a bigger array. Not meant to replace something like [`Microsoft.IO.RecyclableMemoryStream`](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) - this is a much simpler, single-file implementation for when you don't want that dependency.
- **`RentedArray<T>`**: a disposable wrapper around an array rented from an `ArrayPool<T>`, an offset/length view over it, and the pool it came from, so returning it is a `using` away instead of a manual `try`/`finally`.
- **`ArrayPoolExtensions`**: `ReturnSafe`, which clears an array before returning it exactly when `T` is or contains a reference (so pooled reference-typed arrays don't keep otherwise-dead objects alive), and a `Rent<TAs>` overload that rents from an unmanaged-element pool and hands back a differently-typed `Span<TAs>` view sized to fit, for the common case of renting `byte[]` to use as some other unmanaged type.

`BufferSegment<T>` and `SegmentedBufferHelpers` are internal plumbing shared between the above; not part of the public surface.

Targets `net8.0`/`net9.0`/`net10.0`. The project itself builds with `LangVersion=preview` (some of the extension-member syntax used internally needs it), but that's compiled away to ordinary IL - consumers just need an SDK new enough to restore whichever TFM they target, nothing preview-specific.

Contact me on Discord @ `eyeoftheenemy` or open an issue here if you have any questions, suggestions or want to contribute!
