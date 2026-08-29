namespace IcarusStarlink.PakIO.Assets;

/// <summary>A plain 3D vector — kept separate from any WPF/CUE4Parse type so this project stays free of a WPF dependency and the App project stays free of a CUE4Parse one.</summary>
public readonly record struct MeshVector3(float X, float Y, float Z);

/// <summary>A single UV texture coordinate.</summary>
public readonly record struct MeshUv(float U, float V);

/// <summary>
/// A minimal snapshot of a real Unreal static mesh's own LOD0 geometry — everything WPF's
/// MeshGeometry3D needs (Positions/TriangleIndices/Normals/TextureCoordinates line up 1:1 with its
/// own four collections), with no CUE4Parse types exposed outside this project.
/// </summary>
public sealed record StaticMeshGeometry(
    IReadOnlyList<MeshVector3> Positions,
    IReadOnlyList<int> TriangleIndices,
    IReadOnlyList<MeshVector3> Normals,
    IReadOnlyList<MeshUv> TextureCoordinates);
