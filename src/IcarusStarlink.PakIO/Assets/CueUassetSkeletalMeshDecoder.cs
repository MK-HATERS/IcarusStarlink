using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.RenderCore;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real UE4.27 skeletal mesh parsing via CUE4Parse — mirrors CueUassetStaticMeshDecoder's own
/// manual walk (no CUE4Parse-Conversion exporter) but reads USkeletalMesh.LODModels[0] instead of
/// UStaticMesh.RenderData.LODs[0]. The one real structural difference: a static mesh's LOD keeps
/// one flat PositionVertexBuffer/VertexBuffer pair, while a skeletal mesh's own FStaticLODModel
/// keeps its rest-pose (bind pose) vertex data in VertexBufferGPUSkin (FSkeletalMeshVertexBuffer),
/// which stores it in exactly one of four parallel arrays depending on the asset's own precision/
/// packing choice (VertsFloat/VertsHalf/VertsFloatPacked/VertsHalfPacked — only one is ever
/// populated for a given mesh) rather than always the same shape. FSkelMeshVertexBase (every
/// element's own base type) already exposes Pos/Normal/UV uniformly for the two unpacked variants;
/// the two packed variants store Pos as a quantized FVectorIntervalFixed32GPU instead, unpacked
/// here via its own ToVector(MeshOrigin, MeshExtension) — both confirmed present on the installed
/// CUE4Parse 1.2.2.202608 via direct reflection against its own compiled types, not guessed.
///
/// Bind pose only, deliberately: this reads the rest-pose position/normal CUE4Parse already
/// resolved, never touching bone weights (FSkinWeightInfo) or applying any skinning transform —
/// this app has no skinning math today, and animating this preview is out of scope for good.
/// Chunks/Sections' own legacy per-section SoftVertices arrays (the pre-GPU-skin storage format)
/// are deliberately not read at all — VertexBufferGPUSkin is what a real UE4.27 asset actually
/// populates, the same "trust the modern path, not a decades-old fallback" assumption
/// CueUassetStaticMeshDecoder already makes for its own vertex buffers.
/// </summary>
public sealed class CueUassetSkeletalMeshDecoder : IUassetSkeletalMeshDecoder
{
    public StaticMeshGeometry? TryDecodeSkeletalMesh(string modFolderPath, string relativeAssetPath)
    {
        if (!string.Equals(Path.GetExtension(relativeAssetPath), ".uasset", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var mesh = CueAssetProviderLocator.TryLoadExport<USkeletalMesh>(modFolderPath, relativeAssetPath);
            var lod0 = mesh?.LODModels?.FirstOrDefault();
            if (lod0?.VertexBufferGPUSkin is not { } vertexBuffer || lod0.Indices is not { } indexBuffer)
            {
                return null;
            }

            var (positions, normals, textureCoordinates) = ExtractVertices(vertexBuffer);
            if (positions is null)
            {
                return null;
            }

            var vertexCount = positions.Count;
            var triangleIndices = new List<int>(indexBuffer.Length);
            for (var i = 0; i < indexBuffer.Length; i++)
            {
                var index = indexBuffer[i];
                if (index < 0 || index >= vertexCount)
                {
                    // Same defensive convention as CueUassetStaticMeshDecoder: an out-of-range
                    // index has no bounds check of its own once it reaches WPF's MeshGeometry3D,
                    // so this degrades to "can't decode this mesh" rather than risking undefined
                    // rendering behavior.
                    return null;
                }

                triangleIndices.Add(index);
            }

            if (triangleIndices.Count == 0)
            {
                return null;
            }

            return new StaticMeshGeometry(positions, triangleIndices, normals!, textureCoordinates!);
        }
        catch (Exception)
        {
            // A texture, static mesh, sound, blueprint, or a genuinely corrupt/unsupported asset
            // all land here — same "no preview available" fallback every other decoder here uses.
            return null;
        }
    }

    /// <summary>
    /// Reads whichever of the vertex buffer's four parallel arrays is actually populated for this
    /// mesh into plain position/normal/UV lists — returns null (not a partially-filled result) the
    /// moment any per-vertex assumption doesn't hold, same "can't preview this, don't guess"
    /// posture CueUassetStaticMeshDecoder already takes for its own empty-Normal-array case.
    /// </summary>
    private static (List<MeshVector3>? Positions, List<MeshVector3>? Normals, List<MeshUv>? TextureCoordinates) ExtractVertices(
        FSkeletalMeshVertexBuffer buffer)
    {
        var vertexCount = buffer.GetVertexCount();
        if (vertexCount == 0)
        {
            return (null, null, null);
        }

        var positions = new List<MeshVector3>(vertexCount);
        var normals = new List<MeshVector3>(vertexCount);
        var textureCoordinates = new List<MeshUv>(vertexCount);

        // Appends one vertex's already-resolved position + its own last Normal entry (Unreal's
        // own TangentZ — same [^1] convention CueUassetStaticMeshDecoder's own FStaticMeshUVItem
        // reading already relies on, confirmed there against a real mesh) — false means this
        // vertex's own Normal array is empty, the same "don't guess a normal" bailout.
        bool TryAppend(FVector pos, FPackedNormal[] normal, float u, float v)
        {
            if (normal.Length == 0)
            {
                return false;
            }

            positions.Add(new MeshVector3(pos.X, pos.Y, pos.Z));
            var tangentZ = normal[^1];
            normals.Add(new MeshVector3(tangentZ.X, tangentZ.Y, tangentZ.Z));
            textureCoordinates.Add(new MeshUv(u, v));
            return true;
        }

        if (buffer.VertsFloat.Length > 0)
        {
            foreach (var vert in buffer.VertsFloat)
            {
                var uv = vert.UV.Length > 0 ? vert.UV[0] : default;
                if (!TryAppend(vert.Pos, vert.Normal, uv.U, uv.V))
                {
                    return (null, null, null);
                }
            }
        }
        else if (buffer.VertsHalf.Length > 0)
        {
            foreach (var vert in buffer.VertsHalf)
            {
                var uv = vert.UV.Length > 0 ? vert.UV[0] : default;
                if (!TryAppend(vert.Pos, vert.Normal, (float)uv.U, (float)uv.V))
                {
                    return (null, null, null);
                }
            }
        }
        else if (buffer.VertsFloatPacked.Length > 0)
        {
            foreach (var vert in buffer.VertsFloatPacked)
            {
                var uv = vert.UV.Length > 0 ? vert.UV[0] : default;
                var pos = vert.Pos.ToVector(buffer.MeshOrigin, buffer.MeshExtension);
                if (!TryAppend(pos, vert.Normal, uv.U, uv.V))
                {
                    return (null, null, null);
                }
            }
        }
        else if (buffer.VertsHalfPacked.Length > 0)
        {
            foreach (var vert in buffer.VertsHalfPacked)
            {
                var uv = vert.UV.Length > 0 ? vert.UV[0] : default;
                var pos = vert.Pos.ToVector(buffer.MeshOrigin, buffer.MeshExtension);
                if (!TryAppend(pos, vert.Normal, (float)uv.U, (float)uv.V))
                {
                    return (null, null, null);
                }
            }
        }
        else
        {
            // None of the four arrays actually holds this mesh's own vertex data — an assumption
            // this decoder can't safely proceed past (see this method's own doc comment).
            return (null, null, null);
        }

        return (positions, normals, textureCoordinates);
    }
}
