namespace IcarusStarlink.App.Messages;

/// <summary>The Library's "UE4SS isn't installed" bar asks Settings (which owns installing the loader) to install it.</summary>
public sealed record InstallUe4ssRequestedMessage;

/// <summary>Settings installed, updated or removed the UE4SS loader. The Library re-reads its rows and shows <paramref name="StatusMessage"/>.</summary>
public sealed record Ue4ssLoaderChangedMessage(string StatusMessage);
