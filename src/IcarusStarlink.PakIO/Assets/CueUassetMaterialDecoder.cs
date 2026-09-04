using System.Reflection;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real UE4.27 material parameter parsing via UMaterialInterface.GetParams(CMaterialParams2,
/// EMaterialFormat) — a real instance method on the base CUE4Parse package itself (NOT a
/// CUE4Parse-Conversion exporter), confirmed by direct reflection against the installed CUE4Parse
/// 1.2.2.202608 assembly. AllLayersNoRef mirrors the same format value CUE4Parse's own (currently
/// commented-out) example exporter uses for a full-fidelity export.
///
/// A real risk this decoder can't fully guard against, now confirmed by decompiling the installed
/// CUE4Parse 1.2.2.202608 assembly rather than guessed: GetParams(CMaterialParams2, EMaterialFormat)
/// never actually walks a UMaterialInstance's Parent chain or its own TextureParameterValues/
/// VectorParameterValues overrides at all — that richer walk only exists on the OLDER
/// GetParams(CMaterialParams) overload this decoder doesn't call. But the CMaterialParams2 overload
/// behaves very differently depending on which concrete type it's called against, which is exactly
/// what the fallback below exploits:
///   - UMaterial.GetParams(CMaterialParams2, EMaterialFormat) is a REAL override that walks the
///     material's own Expressions array directly (UMaterialExpressionTextureSampleParameter/
///     TextureBase → Textures, UMaterialExpressionVectorParameter → Colors,
///     UMaterialExpressionScalarParameter → Scalars) — completely independent of
///     CachedExpressionData. A plain base-game "master material" is a UMaterial, so when a modded
///     instance's unresolved Parent resolves to one of these, the fallback is likely to recover
///     real content regardless of UE4.27 vs UE5 cooking.
///   - UMaterialInstance/UMaterialInstanceConstant's own override adds only Switches/BlendMode/
///     ShadingModel and delegates Texture/Color population to UMaterialInterface's base
///     implementation, which populates ENTIRELY from UMaterialInterface.CachedExpressionData — a
///     per-object cooked cache UMaterialInterface.Deserialize only ever populates when the asset's
///     own FUE5ReleaseStreamObjectVersion is >= 14, a UE5-era cooking feature
///     (UMaterialInstanceConstant doesn't override GetParams at all despite carrying its own
///     TextureParameterValues/VectorParameterValues). Whether Icarus's own UE4.27 cook carries that
///     data was never confirmed live, so if a resolved Parent is ITSELF another UMaterialInstance
///     rather than a plain UMaterial, it's genuinely uncertain whether the fallback recovers
///     anything. Confirmed empty against 6 real mod-local materials this session either way, which
///     is consistent with both explanations for that mod-local (UMaterialInstance) case; only a
///     real live test against a resolved base-game Parent (once IBaseGameContentProvider can reach
///     one) can confirm the UMaterial path's more promising behavior in practice.
///
/// The fallback itself: CueAssetProviderLocator (what the mod-local load below goes through) only
/// ever indexes the MOD's own folder, never the base game's own pak content, so a modded material
/// instance whose own Parent points at an existing base-game material has no way to resolve that
/// Parent through the mod-local provider — confirmed live as an unhandled exception out of
/// UMaterialInstance's own get_Parent() (it calls the throwing ResolvedObject.Load&lt;T&gt;(), not
/// the safe TryLoad, when the target package isn't in whatever provider loaded this instance).
/// When the mod-local decode above comes up with zero Textures AND zero Colors, and this
/// instance's own unresolved _parent points somewhere real, ApplyBaseGameFallbackIfNeeded below
/// retries the PARENT's own decode against IBaseGameContentProvider (a second, base-game-only
/// CUE4Parse index, mounted once and cached for the session) instead — the closest available
/// preview when the mod's own override values can't be shown.
/// </summary>
public sealed class CueUassetMaterialDecoder : IUassetMaterialDecoder
{
    // _parent has no public accessor — UMaterialInstance.Parent's own getter calls the throwing
    // Load<T>() directly (confirmed via ildasm), so it can't be used to find an unresolved
    // Parent's own path without risking exactly the exception this is trying to detect. Reflected
    // once (not per call) and used read-only: ResolvedObject.Outer/Name are pure import-table
    // metadata lookups (confirmed via ildasm — no file I/O, can't throw the way _parent.Load()
    // itself can), which is exactly why this reaches for the raw ResolvedObject instead of the
    // public Parent property.
    private static readonly FieldInfo? ParentField =
        typeof(UMaterialInstance).GetField("_parent", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly IBaseGameContentProvider _baseGameContentProvider;

    public CueUassetMaterialDecoder(IBaseGameContentProvider baseGameContentProvider) =>
        _baseGameContentProvider = baseGameContentProvider;

    public UassetMaterialParams? TryDecodeMaterial(string modFolderPath, string relativeAssetPath)
    {
        if (!string.Equals(Path.GetExtension(relativeAssetPath), ".uasset", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // BuildMaterialParams's own GetParams call resolves cross-package Texture references
            // lazily, on first access — so it (and TryGetUnresolvedParentAssetPath, run alongside
            // it for convenience) has to happen INSIDE this projection, while the mod-local
            // provider backing `material` is still open. Calling TryLoadExport instead and
            // processing its result afterward would silently starve every cross-package texture
            // reference the mod's own local material makes (the provider disposes the instant
            // TryLoadExport returns) — see TryLoadExportAndProject's own doc comment for why.
            return CueAssetProviderLocator.TryLoadExportAndProject<UMaterialInterface, UassetMaterialParams>(
                modFolderPath, relativeAssetPath,
                material =>
                {
                    var localResult = BuildMaterialParams(material);
                    var unresolvedParentAssetPath = TryGetUnresolvedParentAssetPath(material);
                    return ApplyBaseGameFallbackIfNeeded(localResult, unresolvedParentAssetPath);
                });
        }
        catch (Exception)
        {
            // A texture, mesh, sound, blueprint, or a genuinely corrupt/unsupported asset all land
            // here — same "no preview available" fallback every other decoder here uses.
            return null;
        }
    }

    /// <summary>
    /// The actual fallback decision + retry, split out from TryDecodeMaterial so it's directly
    /// testable with a fake IBaseGameContentProvider and a hand-built UassetMaterialParams/path —
    /// unlike everything upstream of it in TryDecodeMaterial, neither input here needs a real
    /// CUE4Parse object or a binary .uasset fixture (see this project's own top-level report on
    /// why no such fixture exists in-repo).
    ///
    /// Trigger, precisely: localResult has zero Textures AND zero Colors (the two params
    /// GetParams(CMaterialParams2, AllLayersNoRef) can leave empty — see this class's own top doc
    /// comment) AND unresolvedParentAssetPath is non-null (this instance's own _parent really does
    /// point somewhere the mod-local provider couldn't find — see TryGetUnresolvedParentAssetPath).
    /// Either half failing returns localResult straight back, unchanged, WITHOUT ever touching
    /// _baseGameContentProvider — an ordinary self-contained material (something already showed up
    /// locally) or a material with no external Parent to chase both skip the fallback entirely, so
    /// neither one ever pays the base-game provider's one-time mount cost.
    /// </summary>
    internal UassetMaterialParams ApplyBaseGameFallbackIfNeeded(UassetMaterialParams localResult, string? unresolvedParentAssetPath)
    {
        if (localResult.Textures.Count > 0 || localResult.Colors.Count > 0)
        {
            return localResult;
        }

        if (unresolvedParentAssetPath is null)
        {
            return localResult;
        }

        try
        {
            var parentMaterial = _baseGameContentProvider.TryLoadExport<UMaterialInterface>(unresolvedParentAssetPath);
            return parentMaterial is null ? localResult : BuildMaterialParams(parentMaterial);
        }
        catch (Exception)
        {
            // IBaseGameContentProvider.TryLoadExport is already its own defensive boundary (never
            // throws on its own) — this is the outer safety net for anything BuildMaterialParams
            // itself might still raise against a real base-game asset, same "one bad reach
            // shouldn't take down an otherwise-valid local result" posture as the per-texture catch
            // inside BuildMaterialParams below.
            return localResult;
        }
    }

    private static UassetMaterialParams BuildMaterialParams(UMaterialInterface material)
    {
        var parameters = new CMaterialParams2();
        material.GetParams(parameters, EMaterialFormat.AllLayersNoRef);

        var textures = new List<MaterialTextureParam>();
        foreach (var (name, value) in parameters.Textures)
        {
            if (value is not UTexture2D texture)
            {
                // A texture cube, a render target, or another material used as a texture-like
                // input — none of those go through the same TextureDecoder.Decode path
                // CueUassetTextureDecoder/UassetTexturePngEncoder already use, so this
                // parameter is left out of the list entirely rather than shown with no picture.
                continue;
            }

            try
            {
                if (UassetTexturePngEncoder.TryEncodeToPng(texture) is { } png)
                {
                    textures.Add(new MaterialTextureParam(name, png));
                }
            }
            catch (Exception)
            {
                // One bad referenced texture shouldn't take down this material's otherwise-good
                // parameter list.
            }
        }

        var scalars = parameters.Scalars
            .Select(entry => new MaterialScalarParam(entry.Key, entry.Value))
            .ToList();

        var colors = parameters.Colors
            .Select(entry => new MaterialColorParam(entry.Key, entry.Value.R, entry.Value.G, entry.Value.B, entry.Value.A))
            .ToList();

        return new UassetMaterialParams(
            textures, scalars, colors, parameters.BlendMode.ToString(), parameters.ShadingModel.ToString());
    }

    /// <summary>
    /// Finds the package path of this material instance's own unresolved Parent, if it has one —
    /// reading _parent's raw, pre-load Name/Outer metadata (see the ParentField doc comment above
    /// for why this reaches for the private field at all) rather than calling the public Parent
    /// property, which would call the throwing Load&lt;T&gt;() directly.
    ///
    /// Returns null — "nothing to fall back to" — whenever: material isn't a UMaterialInstance at
    /// all (a plain UMaterial has no Parent concept); _parent itself is null (no Parent was ever
    /// set); its package path can't be read; or ANY of this reflection-dependent path throws —
    /// deliberately defensive against a future CUE4Parse version renaming/removing the field, so
    /// this degrades to "no fallback available" rather than a hard failure either way.
    /// </summary>
    private static string? TryGetUnresolvedParentAssetPath(UMaterialInterface material)
    {
        if (material is not UMaterialInstance)
        {
            return null;
        }

        var parentField = ParentField;
        if (parentField is null)
        {
            return null;
        }

        try
        {
            if (parentField.GetValue(material) is not ResolvedObject parent)
            {
                return null;
            }

            // parent.Outer is the package-level import this material object itself sits inside —
            // its own Name IS the full package path (e.g. "/Game/Weapons/Materials/M_BaseWeapon"),
            // Unreal's own convention for how a package names itself in the import table.
            var packagePath = parent.Outer?.Name.Text;
            if (string.IsNullOrEmpty(packagePath))
            {
                return null;
            }

            // Package paths are always rooted at the "Game" virtual mount alias UE4 reserves for a
            // project's own primary content — not a real file-system segment — so it's stripped
            // the same way every mod-local relativeAssetPath this project already works with never
            // includes it (see CueAssetProviderLocator's own doc comment).
            var relativePath = packagePath.TrimStart('/');
            if (relativePath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath["Game/".Length..];
            }

            return relativePath.Length == 0 ? null : relativePath + ".uasset";
        }
        catch (Exception)
        {
            return null;
        }
    }
}
