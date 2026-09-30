#if !NETCOREAPP
using System.Reflection;

namespace LaquaiLib.Buffers;

internal static class ListAccessors<T>
{
    private static readonly FieldInfo _itemsField = typeof(List<T>).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T[] _items(List<T> l) => Unsafe.As<T[]>(_itemsField.GetValue(l));
}
#endif