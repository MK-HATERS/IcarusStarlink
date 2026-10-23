using CommunityToolkit.Mvvm.Messaging.Messages;

namespace IcarusStarlink.App.Messages;

/// <summary>
/// Asked before the app closes itself for an update: a page holding edits not saved yet replies
/// with what they are ("your Saves page edits"). The window's own close prompt can't stop an
/// update's shutdown, so the update is refused until they're saved or reloaded (review, 2026-10-23).
/// </summary>
public sealed class UnsavedEditsRequestMessage : CollectionRequestMessage<string>;
