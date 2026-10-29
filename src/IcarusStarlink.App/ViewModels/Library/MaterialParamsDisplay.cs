namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The WPF-ready counterpart of PakIO's own UassetMaterialParams — LibraryItemViewModel's public
/// SelectedAssetMaterialParams surface never exposes that raw PakIO record directly (its own
/// texture parameters are still un-decoded PNG bytes), same "convert to a WPF-ready type before
/// exposing it as a bound property" precedent BuildMeshModel already sets for SelectedAssetMesh.
/// </summary>
public sealed record MaterialParamsDisplay(
    IReadOnlyList<MaterialTextureParamDisplay> Textures,
    IReadOnlyList<MaterialScalarParamDisplay> Scalars,
    IReadOnlyList<MaterialColorParamDisplay> Colors,
    string BlendMode,
    string ShadingModel);
