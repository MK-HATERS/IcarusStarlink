using CUE4Parse.UE4.Assets.Exports.StaticMesh;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real UE4.27 static mesh parsing via CUE4Parse. Confirmed by direct prototyping against a real
/// mod's own bundled static mesh (JimK_Weapons_Pack_1's AssaultRifleB_Ammo): a per-vertex
/// FStaticMeshUVItem.Normal is a 3-entry FPackedNormal array where only the LAST entry is a
/// genuine unit vector — the other two are tangent-basis placeholders CUE4Parse doesn't need to
/// reconstruct for a shape preview — so index [^1] is Unreal's own "TangentZ", the real normal.
/// That was confirmed against exactly one real mesh, not proven for every possible vertex format,
/// so an empty Normal array here degrades to "can't decode this mesh" rather than throwing.
/// </summary>
public sealed class CueUassetStaticMeshDecoder : IUassetStaticMeshDecoder
{
    public StaticMeshGeometry? TryDecodeStaticMesh(string modFolderPath, string relativeAssetPath)
    {
        if (!string.Equals(Path.GetExtension(relativeAssetPath), ".uasset", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var mesh = CueAssetProviderLocator.TryLoadExport<UStaticMesh>(modFolderPath, relativeAssetPath);
            var lod0 = mesh?.RenderData?.LODs?.FirstOrDefault();
            if (lod0?.PositionVertexBuffer?.Verts is not { } vertexPositions
                || lod0.VertexBuffer?.UV is not { } vertexUvItems
                || lod0.IndexBuffer is not { } indexBuffer)
            {
                return null;
            }

            var vertexCount = vertexPositions.Length;
            if (vertexCount == 0 || vertexUvItems.Length != vertexCount)
            {
                return null;
            }

            var positions = new List<MeshVector3>(vertexCount);
            var normals = new List<MeshVector3>(vertexCount);
            var textureCoordinates = new List<MeshUv>(vertexCount);
            for (var i = 0; i < vertexCount; i++)
            {
                var vertex = vertexPositions[i];
                positions.Add(new MeshVector3(vertex.X, vertex.Y, vertex.Z));

                var uvItem = vertexUvItems[i];
                if (uvItem.Normal.Length == 0)
                {
                    // Confirmed live against exactly one real mesh's own vertex format — an empty
                    // array here means this mesh's shape doesn't match that assumption, not that
                    // it's safe to guess a normal, so the whole mesh is reported as undecodable
                    // rather than rendered with fabricated lighting data.
                    return null;
                }

                var normal = uvItem.Normal[^1];
                normals.Add(new MeshVector3(normal.X, normal.Y, normal.Z));

                var uv = uvItem.UV.Length > 0 ? uvItem.UV[0] : default;
                textureCoordinates.Add(new MeshUv(uv.U, uv.V));
            }

            var triangleIndices = new List<int>(indexBuffer.Length);
            for (var i = 0; i < indexBuffer.Length; i++)
            {
                var index = indexBuffer[i];
                if (index < 0 || index >= vertexCount)
                {
                    // A corrupt or genuinely unsupported mesh could reference a vertex that doesn't
                    // exist — feeding that straight into WPF's MeshGeometry3D has no bounds check
                    // of its own, so this degrades to "can't decode this mesh" the same way an
                    // empty Normal array does, rather than risking undefined rendering behavior.
                    return null;
                }

                triangleIndices.Add(index);
            }

            if (triangleIndices.Count == 0)
            {
                return null;
            }

            return new StaticMeshGeometry(positions, triangleIndices, normals, textureCoordinates);
        }
        catch (Exception)
        {
            // A texture, blueprint, sound, skeletal mesh, or a genuinely corrupt/unsupported asset
            // all land here — the same "no preview available" fallback the Files tab already shows
            // for anything it can't decode, rather than surfacing a raw parser exception.
            return null;
        }
    }
}
