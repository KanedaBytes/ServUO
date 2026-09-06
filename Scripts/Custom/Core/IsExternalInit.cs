// IsExternalInit — the net48 polyfill that makes `init` accessors compile.
//
// C# 9's init-only setters are a compiler feature, but the compiler requires a type named
// System.Runtime.CompilerServices.IsExternalInit to exist somewhere in the compilation. .NET 5+
// ships one in the BCL; net48 does not, so `{ get; init; }` fails to compile with
// "IsExternalInit is not defined or imported" until this file exists.
//
// It must live in that exact namespace and be a plain empty static class. Nothing ever references
// it by name; the compiler finds it by full name and emits a modreq against it.
//
// Paired with <LangVersion>latest</LangVersion> in Scripts/Scripts.csproj — see
// Scripts/Custom/MODIFICATIONS.md entry 4. Nothing in the tree uses `init` yet; this lands with
// the language-level change so that the first file that wants one simply works.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
