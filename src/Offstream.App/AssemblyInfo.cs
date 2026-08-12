using System.Resources;
using System.Windows;

// English is compiled into this assembly; every other language ships as a satellite.
//
// Declared here rather than through the <NeutralResourcesLanguage> MSBuild property because WPF
// compiles a temporary assembly for XAML that references local types, and that temp project does
// not inherit the generated attribute — so CA1824 fails the build there and only there. A source
// attribute is seen by both compilations.
[assembly: NeutralResourcesLanguage("en")]

// Theme resources live in this assembly (Themes/Tokens.xaml), not in a generic theme dictionary.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
