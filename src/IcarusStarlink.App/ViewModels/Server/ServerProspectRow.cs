using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One saved world in the Worlds tab: name, when the server last wrote it, size, and whether it is the one that loads on start.</summary>
public sealed partial class ServerProspectRow(ProspectFile file) : ObservableObject
{
    public string Name => file.Name;

    public DateTime ModifiedLocal => file.ModifiedUtc.ToLocalTime();

    public long SizeBytes => file.SizeBytes;

    /// <summary>Settings → "Load prospect on start" names this world (follows the form as you type).</summary>
    [ObservableProperty]
    private bool _isLoadOnStart;

    /// <summary>The ini's LastProspectName: the world the server ran last.</summary>
    [ObservableProperty]
    private bool _isLastPlayed;
}
