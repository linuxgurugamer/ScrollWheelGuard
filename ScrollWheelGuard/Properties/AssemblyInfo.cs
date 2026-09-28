using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("ScrollWheelGuard")]
[assembly: AssemblyDescription("Blocks the mouse wheel from reaching the KSP camera while the cursor is over an IMGUI window.")]
[assembly: AssemblyProduct("ScrollWheelGuard")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("0.1.3.0")]

// Lets other mods declare a dependency with [assembly: KSPAssemblyDependency("ScrollWheelGuard", 1, 0)].
[assembly: KSPAssembly("ScrollWheelGuard", 1, 0)]
