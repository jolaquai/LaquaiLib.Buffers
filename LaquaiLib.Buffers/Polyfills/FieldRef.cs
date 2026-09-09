#if !NETCOREAPP
using System.Reflection;

namespace LaquaiLib.Buffers;

internal static class ListAccessors<T>
{
    private static readonly FieldInfo _itemsField = typeof(List<T>).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo _sizeField = typeof(List<T>).GetField("_size", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo _versionField = typeof(List<T>).GetField("_version", BindingFlags.NonPublic | BindingFlags.Instance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T[] _items(List<T> l) => Unsafe.As<T[]>(_itemsField.GetValue(l));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static int _size(List<T> l) => (int)_sizeField.GetValue(l);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static int _version(List<T> l) => (int)_versionField.GetValue(l);
}
#endif