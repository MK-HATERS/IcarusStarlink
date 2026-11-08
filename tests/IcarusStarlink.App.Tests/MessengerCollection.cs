namespace IcarusStarlink.App.Tests;

/// <summary>
/// Test classes whose view models register on or send through WeakReferenceMessenger.Default (most
/// page view models; MergeInstallViewModel, GameEditorViewModel, WeeklyChangesViewModel and
/// OwnModWriter take their own IMessenger instead) join this collection so they run one after
/// another. Send runs every handler on the sender's thread, so in parallel a Settings test's UE4SS
/// install made a Library test's rows reload under it mid-assert. Test classes that set other
/// process-wide state another member asserts (UiScale.Current) join it too. A test class split
/// over several partial files carries the attribute on one file only (a second one is CS0579).
/// </summary>
[CollectionDefinition(Name)]
public sealed class MessengerCollection
{
    public const string Name = "App-wide messenger";
}
