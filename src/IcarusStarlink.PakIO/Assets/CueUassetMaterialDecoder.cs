using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real UE4.27 material parameter parsing via UMaterialInterface.GetParams(CMaterialParams2,
/// EMaterialFormat) — a real instance method on the base CUE4Parse package itself (NOT a
/// CUE4Parse-Conversion exporter), confirmed by direct reflection against the installed CUE4Parse
/// 1.2.2.202608 assembly. AllLayersNoRef mirrors the same format value CUE4Parse's own (currently
/// commented-out) example exporter uses for a full-fidelity export — it walks a
/// UMaterialInstanceConstant's own parent chain, resolving every override down to plain
/// textures/colors/scalars, rather than leaving per-layer values as unresolved references.
///
/// A real, NOT yet live-tested risk this decoder can't fully guard against: a modded material
/// overwhelmingly overrides an EXISTING base-game material as its own Parent, and GetParams walking
/// that parent chain needs the parent object actually resolved — but CueAssetProviderLocator (what
/// every decoder here goes through) only ever indexes the MOD's own folder, never the base game's
/// own pak content. Whether CUE4Parse's own GetParams degrades gracefully (silently skipping an
/// unresolvable parent) or throws reaching into one was never confirmed against a real modded
/// material instance — this decoder's own outer try/catch is what stands between that and a crash
/// either way, degrading to "can't decode this material" exactly like everything else it can't
/// parse, but a parent-chain failure specifically is flagged in this project's own top-level report
/// as needing a human's real live test.
/// </summary>
public sealed class CueUassetMaterialDecoder : IUassetMaterialDecoder
{
    public UassetMaterialParams? TryDecodeMaterial(string modFolderPath, string relativeAssetPath)
    {
        if (!string.Equals(Path.GetExtension(relativeAssetPath), ".uasset", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var material = CueAssetProviderLocator.TryLoadExport<UMaterialInterface>(modFolderPath, relativeAssetPath);
            if (material is null)
            {
                return null;
            }

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
        catch (Exception)
        {
            // A texture, mesh, sound, blueprint, or a genuinely corrupt/unsupported asset all land
            // here — same "no preview available" fallback every other decoder here uses.
            return null;
        }
    }
}
