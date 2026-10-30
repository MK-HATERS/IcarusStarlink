using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The row's Nexus actions: Open on Nexus and the endorse / won't-endorse buttons.</summary>
public sealed partial class LibraryItemViewModel
{
    /// <summary>
    /// Closes a stale gap: the context menu item was hardcoded disabled since Phase 3.5 ("Available
    /// once Downloads links mods to their Nexus page"), but NexusModId has existed on LibraryEntry
    /// since this session's own update-checking work — the data this needs was already there, it
    /// just was never wired up. Self-contained on this row's own ViewModel (not LibraryViewModel)
    /// since opening a URL needs nothing else, and doing it here sidesteps ContextMenu's DataContext
    /// not flowing the same way RelativeSource lookups do elsewhere in this app.
    /// </summary>
    [RelayCommand]
    private void OpenOnNexus()
    {
        if (NexusModId is not { } nexusModId)
        {
            return;
        }

        UrlOpener.TryOpen(NexusModWebUrl.For(nexusModId));
    }

    /// <summary>
    /// Nexus's own recorded status for THIS account on this mod — null until "Check endorsements"
    /// has actually run (never persisted, same "computed at display time" precedent as
    /// LatestVersion below — a stale cached endorsement status surviving a restart
    /// would be actively misleading if it changed on Nexus's own website in the meantime).
    /// </summary>
    [ObservableProperty]
    private NexusEndorsementStatus? _endorsementStatus;

    public string EndorsementStatusDisplay => EndorsementStatus switch
    {
        NexusEndorsementStatus.Endorsed => "Endorsed",
        NexusEndorsementStatus.Abstained => "Not endorsing",
        NexusEndorsementStatus.Undecided => "Not endorsed yet",
        _ => "",
    };

    [ObservableProperty]
    private bool _isEndorsing;

    /// <summary>The signed-in Nexus account uploaded this mod: Nexus refuses an author's own endorsement, so no thumb and no Endorse.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEndorseThumb))]
    [NotifyCanExecuteChangedFor(nameof(EndorseCommand), nameof(AbstainCommand))]
    private bool _isOwnNexusMod;

    /// <summary>The row's endorse thumb: a Nexus mod someone else made.</summary>
    public bool ShowEndorseThumb => HasNexusLink && !IsOwnNexusMod;

    partial void OnEndorsementStatusChanged(NexusEndorsementStatus? value)
    {
        OnPropertyChanged(nameof(EndorsementStatusDisplay));
        OnPropertyChanged(nameof(IsEndorsed));
        EndorseCommand.NotifyCanExecuteChanged();
        AbstainCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEndorsingChanged(bool value)
    {
        EndorseCommand.NotifyCanExecuteChanged();
        AbstainCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEndorse), FlowExceptionsToTaskScheduler = true)]
    private Task Endorse() => SetEndorsementAsync(endorse: true);

    private bool CanEndorse() => ShowEndorseThumb && !IsEndorsing && EndorsementStatus != NexusEndorsementStatus.Endorsed;

    [RelayCommand(CanExecute = nameof(CanAbstain), FlowExceptionsToTaskScheduler = true)]
    private Task Abstain() => SetEndorsementAsync(endorse: false);

    private bool CanAbstain() => ShowEndorseThumb && !IsEndorsing && EndorsementStatus != NexusEndorsementStatus.Abstained;

    /// <summary>
    /// A real write to this account's Nexus endorsement for this mod. Fetches the mod's own
    /// CURRENT real Nexus version first rather than reusing this row's own locally-recorded
    /// Version, which can be stale (this mod may have updated since it was downloaded) — Nexus's
    /// own endorse endpoint requires a version that genuinely exists for the mod right now.
    /// </summary>
    private async Task SetEndorsementAsync(bool endorse)
    {
        if (NexusModId is not { } modId)
        {
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            _reportStatus("Sign in with your Nexus API key in Settings to endorse mods.");
            return;
        }

        IsEndorsing = true;
        try
        {
            var info = await _nexusApiClient.GetModInfoAsync(apiKey, NxmHandlerCommand.IcarusDomain, modId);
            if (info is null)
            {
                _reportStatus($"Couldn't reach '{Name}' on Nexus.");
                return;
            }

            EndorsementStatus = await _nexusApiClient.SetEndorsementAsync(apiKey, NxmHandlerCommand.IcarusDomain, modId, info.Version, endorse);
            _reportStatus(endorse ? $"Endorsed '{Name}' on Nexus." : $"Told Nexus you won't endorse '{Name}'.");
        }
        catch (Exception ex)
        {
            _reportStatus($"Couldn't update your endorsement for '{Name}': {ex.Message}");
        }
        finally
        {
            IsEndorsing = false;
        }
    }
}
