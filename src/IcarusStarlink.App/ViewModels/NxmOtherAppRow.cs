using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One row of Settings › Nexus › "Links for other games": a mod manager Starlink replaced as the
/// nxm:// handler and the games whose links it's passed. Typing in Games saves at once.
/// </summary>
public sealed partial class NxmOtherAppRow : ObservableObject
{
    private readonly INxmProtocolRegistrar _registrar;

    public NxmOtherAppRow(NxmOtherApp app, bool getsUnclaimedGames, INxmProtocolRegistrar registrar)
    {
        _registrar = registrar;
        Command = app.Command;
        Name = NxmHandlerWatch.HandlerName(app.Command);
        ExePath = NxmHandlerCommand.ExePathOf(app.Command) ?? app.Command;
        GetsUnclaimedGames = getsUnclaimedGames;
        _games = string.Join(", ", app.Games);
    }

    public string Command { get; }

    public string Name { get; }

    public string ExePath { get; }

    /// <summary>The app replaced last: links for games no row names go here.</summary>
    public bool GetsUnclaimedGames { get; }

    [ObservableProperty]
    private string _games;

    partial void OnGamesChanged(string value) => _registrar.SetOtherAppGames(Command, value);
}
