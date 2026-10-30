using System.Runtime.InteropServices;

// Native DLLs (user32, shell32, dwmapi, mapi32...) are only loaded from System32, never from the
// application or current folder (DLL planting).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
