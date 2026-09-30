#if !NET11_0_OR_GREATER
namespace System.Runtime.CompilerServices;

internal sealed class IsClosedTypeAttribute : Attribute
{
    public IsClosedTypeAttribute() { }
}
#endif