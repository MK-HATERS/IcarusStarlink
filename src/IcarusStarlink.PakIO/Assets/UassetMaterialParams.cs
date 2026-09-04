namespace IcarusStarlink.PakIO.Assets;

/// <summary>One texture-valued material parameter, already decoded to PNG bytes (see CueUassetMaterialDecoder — reuses CueUassetTextureDecoder's own encoding path via UassetTexturePngEncoder). A texture parameter that couldn't itself be decoded (a texture cube, a render target, another material used as a texture-like input, or a genuinely corrupt referenced texture) is left out of the list entirely rather than included with no picture.</summary>
public readonly record struct MaterialTextureParam(string Name, byte[] PngBytes);

/// <summary>One scalar (float) material parameter.</summary>
public readonly record struct MaterialScalarParam(string Name, float Value);

/// <summary>One color (linear RGBA, each 0..1 as Unreal itself stores them — not yet gamma-corrected to sRGB 0..255) material parameter.</summary>
public readonly record struct MaterialColorParam(string Name, float R, float G, float B, float A);

/// <summary>
/// A real compiled Unreal material's own resolved parameter list — not a rendered preview, just
/// what the material actually sets. BlendMode/ShadingModel are already-resolved display strings
/// (CMaterialParams2's own EBlendMode/EMaterialShadingModel enum values, e.g. "BLEND_Opaque",
/// "MSM_DefaultLit") rather than the raw enum types, so IcarusStarlink.App's ViewModel layer never
/// needs a CUE4Parse reference of its own to show them — same "keep CUE4Parse types out of the App
/// project" boundary StaticMeshGeometry's own doc comment already establishes for mesh geometry.
/// </summary>
public sealed record UassetMaterialParams(
    IReadOnlyList<MaterialTextureParam> Textures,
    IReadOnlyList<MaterialScalarParam> Scalars,
    IReadOnlyList<MaterialColorParam> Colors,
    string BlendMode,
    string ShadingModel);
