using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// A row the tree view (TalentTreeViewModel) can draw: a character's talent, workshop research or
/// a mission (SaveTalentViewModel), or a mount's talent (MountTalentViewModel). The node template
/// also binds RankDisplay, Description and Model by name.
/// </summary>
public interface ITreeTalent : INotifyPropertyChanged
{
    /// <summary>Its D_Talents row.</summary>
    string RowName { get; }

    string DisplayName { get; }

    /// <summary>Its D_Talents tree row ("Survival_Resources"): where the tree's layout comes from.</summary>
    string Tree { get; }

    /// <summary>The heading it's listed under; one tree on the tree view per heading.</summary>
    string Group { get; }

    int GroupOrder { get; }

    int Rank { get; set; }

    int MaxRank { get; }

    bool IsDefaultUnlocked { get; }

    /// <summary>Has it in game: a rank, or the game gives it from the start.</summary>
    bool IsUnlockedInGame { get; }

    bool IsMaxed { get; }

    /// <summary>Changed since the last load or save.</summary>
    bool IsDirty { get; }

    /// <summary>Its research cost ("40 Tech"), on the node's tooltip; null hides the line.</summary>
    string? Cost { get; }

    /// <summary>The DLC pack it needs, on the node's tooltip; null hides the line.</summary>
    string? PackNote { get; }

    string? IconPath { get; }

    ImageSource? Icon { get; set; }

    ICommand RankUpCommand { get; }

    ICommand RankDownCommand { get; }

    /// <summary>Back to the rank it had at the last load or save.</summary>
    void Revert();
}
