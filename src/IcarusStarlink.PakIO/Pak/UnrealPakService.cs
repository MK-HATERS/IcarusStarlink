namespace IcarusStarlink.PakIO.Pak;

/// <summary>
/// Wraps UnrealPak.exe (Epic's own pak (un)packing tool, not something this app implements itself)
/// to extract the one pak file this app actually needs: Content\Data\data.pak. Confirmed against a
/// real Icarus install — the game ships 33 separate pakchunk*-WindowsNoEditor.pak files under
/// Content\Paks (assets: models/textures/audio, per the user's own Notes5SQL.ini mapping), entirely
/// separate from Content\Data\data.pak, which holds just the gameplay DataTable JSON
/// (Crafting\D_ProcessorRecipes.json, Traits\D_Fuel.json, ~300 files across ~75 category folders)
/// that EXMOD mods actually target and this app's diff/merge engine actually reads. Also confirmed:
/// no AES key is needed to extract it — UnrealPak.exe pulls it apart directly.
/// </summary>
public sealed class UnrealPakService(IProcessRunner processRunner) : IUnrealPakService
{
    public async Task<UnrealPakExtractResult> ExtractDataPakAsync(
        string unrealPakExePath, string icarusContentPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unrealPakExePath) || !File.Exists(unrealPakExePath))
        {
            throw new FileNotFoundException($"UnrealPak.exe not found at '{unrealPakExePath}' — check the UnrealPak.exe path setting.");
        }

        var dataPakPath = Path.Combine(icarusContentPath, "Data", "data.pak");
        if (!File.Exists(dataPakPath))
        {
            throw new FileNotFoundException(
                $"'{dataPakPath}' doesn't exist — check the Icarus Content folder setting (it should be the "
                + "…\\Icarus\\Icarus\\Content folder, the one containing a Data subfolder with data.pak in it).");
        }

        // Replacing rather than merging into whatever's already there: a stale DataTable file left
        // over from a previous game version (renamed/removed field, dropped table) sitting alongside
        // this run's fresh extract would silently misrepresent the game's *current* data — Update
        // data folder is meant to produce one true current snapshot, not an accumulating pile.
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
        Directory.CreateDirectory(outputDirectory);

        var result = await processRunner.RunAsync(unrealPakExePath, [dataPakPath, "-Extract", outputDirectory], cancellationToken);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"UnrealPak.exe exited with code {result.ExitCode}: {detail}");
        }

        var extractedFileCount = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories).Length;
        return new UnrealPakExtractResult(extractedFileCount);
    }
}
