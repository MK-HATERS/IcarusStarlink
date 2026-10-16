namespace IcarusStarlink.App.Tests;

/// <summary>
/// The app talks between pages through one process-wide WeakReferenceMessenger (LibraryChangedMessage,
/// Ue4ssLoaderChangedMessage, …). Test classes whose view models send or react to those messages
/// share this collection so they run one after another — run in parallel, a Settings test's UE4SS
/// install made a Library test's rows reload under it mid-assert.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MessengerCollection
{
    public const string Name = "App-wide messenger";
}
