using System.Runtime.InteropServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// Native DLLs (user32, shell32, dwmapi...) are only loaded from System32, never from the
// application or current folder (DLL planting).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
