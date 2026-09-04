using System.Runtime.CompilerServices;
using System.Windows;

// So SavesViewModelTests can await WaitForPendingSlotLoadAsync() — an internal test-only seam for
// the fire-and-forget slot load OnSelectedSlotChanged kicks off, see its own doc comment. Mirrors
// IcarusStarlink.PakIO's own InternalsVisibleTo grant to its test project.
[assembly: InternalsVisibleTo("IcarusStarlink.App.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
