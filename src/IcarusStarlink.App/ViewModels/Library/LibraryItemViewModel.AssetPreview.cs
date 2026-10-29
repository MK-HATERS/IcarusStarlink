using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The Files tab's preview of the picked file: text, image, mesh, sound or material, for EXMOD folders and packed paks.</summary>
public sealed partial class LibraryItemViewModel
{
    partial void OnSelectedAssetPathChanged(string? value)
    {
        SelectedAssetImage = null;
        SelectedAssetMesh = null;
        SelectedAssetMaterialParams = null;
        CleanupAudioTempFile();
        _assetPreviewGeneration++;
        var generation = _assetPreviewGeneration;

        if (value is null)
        {
            SelectedAssetPreview = null;
            return;
        }

        if (IsOpaquePak)
        {
            // These paths came from UnrealPak -List, not this mod's own folder on disk —
            // ReadAssetContent (ExmodFolder-based) has nothing to read them from. A .uasset is
            // still worth a real decode attempt (see DecodeOpaquePakAssetPreviewAsync below,
            // which extracts this mod's own .pak and runs it through the same decoders a regular
            // EXMOD mod's preview already uses) — everything else here (.uexp, .ubulk, or a
            // non-asset file) has no decoder to try and falls back to a plain "no preview"
            // message, the same as any file type DecodeCompiledAssetPreviewAsync can't show.
            if (string.Equals(Path.GetExtension(value), ".uasset", StringComparison.OrdinalIgnoreCase))
            {
                SelectedAssetPreview = "Decoding this asset...";
                _ = DecodeOpaquePakAssetPreviewAsync(value, generation);
                return;
            }

            SelectedAssetPreview = "(packed inside this .pak — no preview available for this file type)";
            return;
        }

        try
        {
            // Checked before any read: DecodeCompiledAssetPreviewAsync does its own async decode
            // straight from disk (via FolderName's own path + this relative path) and never touches
            // this method's own `bytes` — reading a compiled asset's full content synchronously
            // here first, only to discard it, was pure wasted work blocking the UI thread for
            // nothing (a large mesh/texture .uasset can be many MB). Found live.
            if (string.Equals(Path.GetExtension(value), ".uasset", StringComparison.OrdinalIgnoreCase))
            {
                SelectedAssetPreview = "Decoding this asset...";
                _ = DecodeCompiledAssetPreviewAsync(value, generation);
                return;
            }

            var bytes = _repository.ReadAssetContent(FolderName, value);

            // The spec's "preview text/images" — an image asset used to render as binary noise in
            // the text box, since every non-text file fell through to the byte-count message.
            if (ImageExtensions.Contains(Path.GetExtension(value)) && TryDecodeImage(bytes) is { } image)
            {
                SelectedAssetImage = image;
                SelectedAssetPreview = null;
                return;
            }

            SelectedAssetPreview = LooksLikeText(bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : $"(binary file — {bytes.Length:N0} bytes, no preview)";
        }
        catch (Exception ex)
        {
            // Same UI boundary as SaveMetadata: the asset a user just clicked can vanish or get
            // locked between ListAssetPaths populating this list and this read (external edit,
            // AV quarantine), and that should show up as an explanation in the preview pane
            // itself — the one place this specific failure is actually relevant — rather than
            // crash the app out of a binding-driven property setter.
            SelectedAssetPreview = $"(failed to read this file: {ex.Message})";
            _reportStatus($"Preview failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The one non-null slot in CompiledAssetDecodeResult — an asset is realistically only ever one
    /// of these types, so DecodeCompiledAssetPreviewAsync's own chain stops at the first decoder
    /// that succeeds instead of running every one of the (real, CPU-costing) remaining decoders for
    /// nothing. Static and skeletal meshes share the one Mesh slot: both end up rendered through
    /// the exact same BuildMeshModel/Viewport3D path, so nothing downstream needs to tell them apart.
    /// </summary>
    private sealed record CompiledAssetDecodeResult(
        byte[]? PngBytes, StaticMeshGeometry? Mesh, UassetSoundAudio? Sound, UassetMaterialParams? Material);

    /// <summary>
    /// A .uasset is compiled Unreal binary, not something the flat-image/text branches above can
    /// show — this tries, in order, a real Unreal texture, a static mesh, a skeletal mesh (bind
    /// pose only), a sound, then a material, via CUE4Parse — the same "texture first, most-proven
    /// decoders first" order this chain already used before the last three were added. Runs off
    /// the UI thread since indexing the mod's own asset folder and decoding any one of these takes
    /// real time; the generation check discards a stale result if the user has already selected
    /// something else by the time it finishes.
    /// </summary>
    private async Task DecodeCompiledAssetPreviewAsync(string relativeAssetPath, int generation)
    {
        string modFolderPath;
        try
        {
            modFolderPath = _repository.GetFolderPath(FolderName);
        }
        catch (Exception ex)
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = $"(failed to read this file: {ex.Message})";
            }
            return;
        }

        // Fire-and-forget caller: a decoder that throws (rather than returning null) used to leave
        // the pane on "Decoding this asset..." for good, the error lost.
        CompiledAssetDecodeResult result;
        try
        {
            result = await Task.Run(() => DecodeCompiledAsset(modFolderPath, relativeAssetPath));
        }
        catch (Exception ex)
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = $"(couldn't decode this asset: {ex.Message})";
            }

            return;
        }

        if (generation != _assetPreviewGeneration)
        {
            return;
        }

        if (result.PngBytes is not null && TryDecodeImage(result.PngBytes) is { } image)
        {
            SelectedAssetImage = image;
            SelectedAssetPreview = null;
        }
        else if (result.Mesh is not null)
        {
            SelectedAssetMesh = BuildMeshModel(result.Mesh);
            SelectedAssetPreview = null;
        }
        else if (result.Sound is { WavBytes: { } wavBytes })
        {
            try
            {
                SelectedAssetAudioPath = WriteAudioTempFile(wavBytes);
                SelectedAssetPreview = null;
            }
            catch (Exception ex)
            {
                // A real but unlikely failure (disk full, %TEMP% unavailable/locked down) — same UI
                // boundary as everything else in this method: a status message, not an unobserved
                // exception out of this fire-and-forget decode task.
                SelectedAssetPreview = $"(compiled Unreal asset — couldn't write this sound to a temp file for playback: {ex.Message})";
            }
        }
        else if (result.Sound is { UnsupportedFormatReason: { } unsupportedReason })
        {
            SelectedAssetPreview = $"(compiled Unreal asset — {unsupportedReason})";
        }
        else if (result.Material is not null)
        {
            SelectedAssetMaterialParams = BuildMaterialParamsDisplay(result.Material);
            SelectedAssetPreview = null;
        }
        else
        {
            SelectedAssetPreview = "(compiled Unreal asset — not a texture, mesh, sound, or material, or couldn't be decoded — no preview available)";
        }
    }

    private CompiledAssetDecodeResult DecodeCompiledAsset(string modFolderPath, string relativeAssetPath)
    {
        var png = _uassetTextureDecoder.TryDecodeToPng(modFolderPath, relativeAssetPath);
        if (png is not null)
        {
            return new CompiledAssetDecodeResult(png, null, null, null);
        }

        var mesh = _uassetStaticMeshDecoder.TryDecodeStaticMesh(modFolderPath, relativeAssetPath)
            ?? _uassetSkeletalMeshDecoder.TryDecodeSkeletalMesh(modFolderPath, relativeAssetPath);
        if (mesh is not null)
        {
            return new CompiledAssetDecodeResult(null, mesh, null, null);
        }

        var sound = _uassetSoundDecoder.TryDecodeAudio(modFolderPath, relativeAssetPath);
        if (sound is not null)
        {
            return new CompiledAssetDecodeResult(null, null, sound, null);
        }

        var material = _uassetMaterialDecoder.TryDecodeMaterial(modFolderPath, relativeAssetPath);
        return new CompiledAssetDecodeResult(null, null, null, material);
    }

    /// <summary>
    /// Writes a decoded sound's own real, complete WAV bytes to a fresh temp file — WPF's
    /// MediaElement plays from a file/URI, not raw bytes, so a decoded sound has to land on disk
    /// somewhere first. The PREVIOUS temp file (if any) was already deleted by
    /// OnSelectedAssetPathChanged's own CleanupAudioTempFile call before this decode even started,
    /// so there's nothing left over to clean up here — only this new one needs tracking, for
    /// whenever the NEXT selection change comes along.
    /// </summary>
    private string WriteAudioTempFile(byte[] wavBytes)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_AudioPreview_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(tempPath, wavBytes);
        _currentAudioTempFilePath = tempPath;
        return tempPath;
    }

    /// <summary>
    /// ModDetailWindow's own Closed handler calls this (after releasing the MediaElement's file
    /// handle via AudioPlayer.Close()) — without it, closing the window left whatever temp .wav
    /// was last previewed on disk indefinitely: this instance is cached/reused by LibraryViewModel,
    /// so CleanupAudioTempFile's own normal trigger (the NEXT selection change) might not fire
    /// again for the life of the session.
    /// </summary>
    public void ReleaseAudioPreview() => CleanupAudioTempFile();

    /// <summary>
    /// Best-effort delete of whatever temp .wav WriteAudioTempFile last created — called at the
    /// start of every selection change (see OnSelectedAssetPathChanged) and by ReleaseAudioPreview
    /// (see its own doc comment), so at most one of these temp files exists per
    /// LibraryItemViewModel instance at any given time, rather than one accumulating per sound ever
    /// previewed for the life of the app.
    /// </summary>
    private void CleanupAudioTempFile()
    {
        SelectedAssetAudioPath = null;

        if (_currentAudioTempFilePath is not { } path)
        {
            return;
        }

        _currentAudioTempFilePath = null;
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort only, same convention this codebase's other temp-file cleanup already
            // uses (e.g. DownloadsViewModel's own download temp files) — a locked/already-gone
            // file here is cosmetic (an OS temp directory, not mod data), never worth surfacing.
        }
    }

    /// <summary>
    /// Real preview attempt for a .uasset packed inside an opaque/prebuilt-imported pak.
    /// DecodeCompiledAssetPreviewAsync above can point its decoders straight at this mod's own
    /// folder on disk because a regular EXMOD mod's assets already live there loose — an opaque
    /// pak's own assets don't; they're only ever packed inside its single .pak file, which
    /// CueAssetProviderLocator (what every decoder goes through) can't index directly. So this
    /// extracts that pak first — cached under this app's own Cache directory, same convention
    /// ThumbnailImage's own remote-picture cache already uses, keyed by this mod's FolderName so
    /// repeated preview clicks against the same pak don't each pay a fresh whole-pak extract (see
    /// OpaquePakAssetPreviewService's own doc comment for why it's whole-pak, not scoped, and
    /// what that costs the first time) — then runs the exact same five decoders through it. Same
    /// generation-based staleness guard, and the same PngBytes/Mesh/Sound/Material branching, as
    /// DecodeCompiledAssetPreviewAsync.
    /// </summary>
    private async Task DecodeOpaquePakAssetPreviewAsync(string relativeAssetPath, int generation)
    {
        var unrealPakExePath = _settingsService.Current.UnrealPakExePath;
        if (string.IsNullOrWhiteSpace(unrealPakExePath))
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = "couldn't decode this asset: set UnrealPak.exe's path in Settings first";
            }
            return;
        }

        string? pakFilePath;
        try
        {
            pakFilePath = Directory.GetFiles(_repository.GetFolderPath(FolderName), "*.pak").FirstOrDefault();
        }
        catch (Exception ex)
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = $"couldn't decode this asset: {ex.Message}";
            }
            return;
        }

        if (pakFilePath is null)
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = "couldn't decode this asset: couldn't find this mod's own .pak file";
            }
            return;
        }

        var cacheDirectory = Path.Combine(_pakPreviewCacheDirectory, FolderName);
        OpaquePakAssetPreviewResult result;
        try
        {
            result = await _opaquePakAssetPreviewService.PreviewAssetAsync(unrealPakExePath, pakFilePath, relativeAssetPath, cacheDirectory);
        }
        catch (Exception ex)
        {
            // Not expected — OpaquePakAssetPreviewService reports every failure it knows about
            // through its own result type — but this is still a background-thread continuation
            // reached from a binding-driven property setter, the same UI boundary
            // DecodeCompiledAssetPreviewAsync's own callers guard, so an unanticipated exception
            // here becomes a status message instead of crashing the app.
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = $"couldn't decode this asset: {ex.Message}";
            }
            return;
        }

        if (generation != _assetPreviewGeneration)
        {
            return;
        }

        if (result.PngBytes is not null && TryDecodeImage(result.PngBytes) is { } image)
        {
            SelectedAssetImage = image;
            SelectedAssetPreview = null;
        }
        else if (result.Mesh is not null)
        {
            SelectedAssetMesh = BuildMeshModel(result.Mesh);
            SelectedAssetPreview = null;
        }
        else if (result.Sound is { WavBytes: { } wavBytes })
        {
            try
            {
                SelectedAssetAudioPath = WriteAudioTempFile(wavBytes);
                SelectedAssetPreview = null;
            }
            catch (Exception ex)
            {
                // Same UI boundary as DecodeCompiledAssetPreviewAsync's own sound branch: a real
                // but unlikely failure (disk full, %TEMP% unavailable/locked down).
                SelectedAssetPreview = $"(compiled Unreal asset — couldn't write this sound to a temp file for playback: {ex.Message})";
            }
        }
        else if (result.Sound is { UnsupportedFormatReason: { } unsupportedReason })
        {
            SelectedAssetPreview = $"(compiled Unreal asset — {unsupportedReason})";
        }
        else if (result.Material is not null)
        {
            SelectedAssetMaterialParams = BuildMaterialParamsDisplay(result.Material);
            SelectedAssetPreview = null;
        }
        else
        {
            SelectedAssetPreview = $"couldn't decode this asset: {result.FailureReason ?? "unknown reason"}";
        }
    }

    /// <summary>
    /// Converts plain decoded geometry into a real WPF Model3D — a flat-shaded mesh plus its own
    /// small ambient+directional light rig, so the Files tab's Viewport3D can show it without
    /// depending on any scene lighting of its own. Unreal is Z-up — UpDirection is set to match
    /// when framing the camera, rather than transforming any vertex data to WPF's usual Y-up.
    /// </summary>
    private static Model3D BuildMeshModel(StaticMeshGeometry geometry)
    {
        var mesh = new MeshGeometry3D();
        foreach (var position in geometry.Positions)
        {
            mesh.Positions.Add(new Point3D(position.X, position.Y, position.Z));
        }

        foreach (var index in geometry.TriangleIndices)
        {
            mesh.TriangleIndices.Add(index);
        }

        foreach (var normal in geometry.Normals)
        {
            mesh.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
        }

        foreach (var uv in geometry.TextureCoordinates)
        {
            mesh.TextureCoordinates.Add(new Point(uv.U, uv.V));
        }

        mesh.Freeze();

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xA0, 0xA6, 0xB0))));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), 32));
        material.Freeze();

        var geometryModel = new GeometryModel3D(mesh, material) { BackMaterial = material };
        geometryModel.Freeze();

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x60, 0x60, 0x60)));
        group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -2)));
        group.Children.Add(geometryModel);
        group.Freeze();
        return group;
    }

    /// <summary>
    /// Converts PakIO's own plain UassetMaterialParams into the WPF-ready MaterialParamsDisplay the
    /// Files tab's own material panel actually binds to — same "convert before exposing" step
    /// BuildMeshModel already does for SelectedAssetMesh. A texture parameter whose own PNG bytes
    /// don't decode to a real image (shouldn't happen — CueUassetMaterialDecoder only ever adds a
    /// texture parameter after already encoding it to PNG itself — but TryDecodeImage's own
    /// contract is "never throw, return null instead") is left out of the list entirely, the exact
    /// same "don't show what didn't decode" convention MaterialTextureParam's own doc comment
    /// already establishes for a texture that couldn't be decoded in the first place.
    /// </summary>
    private static MaterialParamsDisplay BuildMaterialParamsDisplay(UassetMaterialParams materialParams)
    {
        var textures = new List<MaterialTextureParamDisplay>();
        foreach (var texture in materialParams.Textures)
        {
            if (TryDecodeImage(texture.PngBytes) is { } thumbnail)
            {
                textures.Add(new MaterialTextureParamDisplay(texture.Name, thumbnail));
            }
        }

        var scalars = materialParams.Scalars
            .Select(scalar => new MaterialScalarParamDisplay(scalar.Name, scalar.Value))
            .ToList();

        var colors = materialParams.Colors
            .Select(color => new MaterialColorParamDisplay(
                color.Name,
                Color.FromArgb(ToDisplayByte(color.A), ToDisplayByte(color.R), ToDisplayByte(color.G), ToDisplayByte(color.B)),
                $"R {color.R:0.###}  G {color.G:0.###}  B {color.B:0.###}  A {color.A:0.###}"))
            .ToList();

        return new MaterialParamsDisplay(textures, scalars, colors, materialParams.BlendMode, materialParams.ShadingModel);
    }

    /// <summary>Unreal's own linear-color channels aren't clamped to 0..1 by construction (an emissive color, in particular, can legitimately run well above 1) — clamped here before scaling so an out-of-range value becomes a plain solid swatch color instead of an invalid byte.</summary>
    private static byte ToDisplayByte(float channel) => (byte)(Math.Clamp(channel, 0f, 1f) * 255);

    /// <summary>Decodes image bytes to a frozen bitmap, or null if they aren't a decodable image. Frozen so it can be handed to the UI thread's bindings safely and never re-reads the (already disposed) stream.</summary>
    private static BitmapImage? TryDecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // A file with an image extension that isn't actually a decodable image — treat it as
            // "not an image" rather than an error.
            return null;
        }
    }

    private static bool LooksLikeText(byte[] bytes) =>
        !bytes.Take(Math.Min(bytes.Length, 512)).Any(b => b == 0);
}
