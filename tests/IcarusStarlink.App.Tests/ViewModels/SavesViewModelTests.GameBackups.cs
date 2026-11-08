using System.IO;
using IcarusStarlink.Core.Saves;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Backup &amp; restore › "Kept by the game": the game's own .backup copies of the player files, listed fresh and put back after Starlink's own backup.</summary>
public sealed partial class SavesViewModelTests
{
    private static GameBackupCopy GameCopy(string original, int number, DateTime writtenUtc) =>
        new($@"C:\fake\PlayerData\{SteamId}\{original}.backup{(number == 0 ? "" : $"_{number}")}", original, number, writtenUtc, 100);

    [Fact]
    public async Task GameBackups_AreListedWithTheSlot_AndReadAgainWhenThePageIsShown()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            GameBackups = [GameCopy("Profile.json", 0, DateTime.UtcNow.AddHours(-1)), GameCopy("Profile.json", 3, DateTime.UtcNow.AddHours(-3))],
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Equal(2, vm.GameBackups.Count);
        Assert.True(vm.HasGameBackups);
        Assert.Null(vm.SelectedGameBackup);
        Assert.False(vm.RestoreGameBackupCommand.CanExecute(null));

        // The game saved since: the list is read again, not kept from before.
        repository.GameBackups = [GameCopy("Characters.json", 0, DateTime.UtcNow)];
        vm.OnPageShown();

        Assert.Equal("Characters.json", Assert.Single(vm.GameBackups).OriginalFileName);
    }

    [Fact]
    public async Task RestoreGameBackup_AsksFirst_ThenPutsTheCopyBack_AndReadsTheSlotAgain()
    {
        var copy = GameCopy("Profile.json", 3, DateTime.UtcNow.AddHours(-3));
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            GameBackups = [copy],
        };
        var dialogs = new FakeDialogService(confirmResult: true);
        var vm = CreateViewModel(repository, dialogService: dialogs);
        await vm.WaitForPendingSlotLoadAsync();
        vm.SelectedGameBackup = vm.GameBackups.Single();
        Assert.True(vm.RestoreGameBackupCommand.CanExecute(null));
        var confirmsBefore = dialogs.ConfirmCallCount;

        await vm.RestoreGameBackupCommand.ExecuteAsync(null);

        Assert.Equal(confirmsBefore + 1, dialogs.ConfirmCallCount);
        Assert.Contains("Profile.json", dialogs.LastConfirmMessage);
        Assert.Equal(copy, Assert.Single(repository.RestoredGameBackups));
        Assert.StartsWith("Put back Profile.json", vm.StatusMessage);
    }

    [Fact]
    public async Task RestoreGameBackup_NoToTheQuestion_ChangesNothing()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            GameBackups = [GameCopy("Profile.json", 0, DateTime.UtcNow)],
        };
        var vm = CreateViewModel(repository, dialogService: new FakeDialogService(confirmResult: false));
        await vm.WaitForPendingSlotLoadAsync();
        vm.SelectedGameBackup = vm.GameBackups.Single();

        await vm.RestoreGameBackupCommand.ExecuteAsync(null);

        Assert.Empty(repository.RestoredGameBackups);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreGameBackup_WhileTheGameRuns_IsRefused(bool runningAtFirstCheck)
    {
        // Running from the start, or started while the question was open.
        bool[] running = runningAtFirstCheck ? [true] : [false, true];
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            GameBackups = [GameCopy("Profile.json", 0, DateTime.UtcNow)],
        };
        var vm = CreateViewModel(repository, gameProcessChecker: new FakeGameProcessChecker(running));
        await vm.WaitForPendingSlotLoadAsync();
        vm.SelectedGameBackup = vm.GameBackups.Single();

        await vm.RestoreGameBackupCommand.ExecuteAsync(null);

        Assert.Empty(repository.RestoredGameBackups);
        Assert.Contains("running", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GameBackups_FolderCantBeRead_SaysWhy_InsteadOfNoCopiesYet()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            ListGameBackupsError = new UnauthorizedAccessException("denied"),
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Empty(vm.GameBackups);
        Assert.StartsWith("Couldn't read the game's copies", vm.GameBackupsNote);
        Assert.Contains("denied", vm.GameBackupsNote);

        // Readable again: the note goes back to the plain empty text.
        repository.ListGameBackupsError = null;
        vm.OnPageShown();

        Assert.Equal("no copies yet", vm.GameBackupsNote);
    }

    [Fact]
    public async Task RestoreGameBackup_WhileItWrites_SaveBackupAndRestoreWait()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            GameBackups = [GameCopy("Profile.json", 0, DateTime.UtcNow)],
            Backups = [new SaveBackupInfo("fake_backup.zip", DateTimeOffset.Now)],
            WhileRestoringGameBackup = () =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            },
        };
        var vm = CreateViewModel(repository, dialogService: new FakeDialogService(confirmResult: true));
        await vm.WaitForPendingSlotLoadAsync();
        vm.SelectedGameBackup = vm.GameBackups.Single();

        var restoring = vm.RestoreGameBackupCommand.ExecuteAsync(null);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            var saves = repository.SaveCharactersCallCount;
            var backups = repository.BackupSlotCallCount;
            var restores = repository.RestoreSlotCallCount;
            vm.Characters.Single().Name = "Renamed";

            vm.SaveChangesCommand.Execute(null);
            vm.BackupNowCommand.Execute(null);
            await vm.RestoreBackupCommand.ExecuteAsync(null);

            Assert.Equal(saves, repository.SaveCharactersCallCount);
            Assert.Equal(backups, repository.BackupSlotCallCount);
            Assert.Equal(restores, repository.RestoreSlotCallCount);
            Assert.Contains("Wait", vm.StatusMessage);
            Assert.False(vm.RestoreGameBackupCommand.CanExecute(null));
        }
        finally
        {
            release.Set();
            await restoring;
        }

        Assert.Single(repository.RestoredGameBackups);
        Assert.StartsWith("Put back Profile.json", vm.StatusMessage);
    }

    [Fact]
    public async Task RestoreGameBackup_CopyShiftedSinceListed_SaysSo_AndReadsTheListAgain()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            GameBackups = [GameCopy("Profile.json", 0, DateTime.UtcNow.AddHours(-1))],
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();
        vm.SelectedGameBackup = vm.GameBackups.Single();
        var newer = GameCopy("Profile.json", 0, DateTime.UtcNow);
        repository.GameBackups = [newer];
        repository.RestoreGameBackupError = new InvalidDataException("The game changed its copies since the list was read. Pick one again. Nothing was changed.");

        await vm.RestoreGameBackupCommand.ExecuteAsync(null);

        Assert.StartsWith("Couldn't put the game's copy back", vm.StatusMessage);
        Assert.Equal(newer, Assert.Single(vm.GameBackups));
    }
}
