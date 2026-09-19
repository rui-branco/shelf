using System.Reflection;
using System.Runtime.InteropServices;

// csc turns these into the Win32 version resource. Without them Windows has no
// display name for the exe and falls back to the bare filename ("shelf").
[assembly: AssemblyTitle("Shelf")]
[assembly: AssemblyProduct("Shelf")]
[assembly: AssemblyDescription("Downloads stack for your desktop")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]
