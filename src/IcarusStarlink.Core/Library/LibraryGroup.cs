namespace IcarusStarlink.Core.Library;

public sealed record LibraryGroup(string GroupKey, string DisplayName, IReadOnlyList<LibraryEntry> Entries)
{
    public bool IsFamily => Entries.Count > 1;
}
