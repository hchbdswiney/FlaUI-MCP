// Polyfill required for C# records and init-only setters when targeting .NET Framework 4.8.
// The compiler needs this type to exist; it is provided automatically only on .NET 5+.
#if NETFRAMEWORK
using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
#endif
