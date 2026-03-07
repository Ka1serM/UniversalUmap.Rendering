using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace UniversalUmap.Rendering;

public sealed class MeshAsset : IDisposable
{
    private const float UnrealToRendererScale = 0.01f;
    private static readonly Matrix4x4 UnrealToRendererBasis = new()
    {
        M11 = 0f,
        M21 = 1f,
        M31 = 0f,
        M41 = 0f,
        M12 = 0f,
        M22 = 0f,
        M32 = -1f,
        M42 = 0f,
        M13 = 1f,
        M23 = 0f,
        M33 = 0f,
        M43 = 0f,
        M14 = 0f,
        M24 = 0f,
        M34 = 0f,
        M44 = 1f
    };
    private static readonly bool UnrealToRendererFlipsHandedness = UnrealToRendererBasis.GetDeterminant() < 0f;

    private readonly Context context;
    private readonly Accel? blasRtx;
    private readonly GpuBuffer? bvhNodesBuffer;
    private readonly GpuBuffer? bvhIndicesBuffer;

    public string Name { get; }
    public uint MeshIndex { get; internal set; } = uint.MaxValue;

    internal GpuBuffer VertexBuffer { get; }
    internal GpuBuffer IndexBuffer { get; }
    internal GpuBuffer FaceBuffer { get; }
    internal GpuBuffer MaterialBuffer { get; }

    public bool Dirty { get; private set; }

    public MeshAsset(
        Context context,
        string name,
        IReadOnlyList<Vertex> vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<Face>? faces = null,
        IReadOnlyList<MaterialData>? materials = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Mesh name is empty", nameof(name));
        if (vertices.Count == 0)
            throw new ArgumentException("Mesh requires at least one vertex", nameof(vertices));
        if (indices.Count == 0 || indices.Count % 3 != 0)
            throw new ArgumentException("Mesh indices must contain at least one triangle (count % 3 == 0)", nameof(indices));

        var vertexArray = vertices.ToArray();
        var indexArray = indices.ToArray();

        var faceArray = faces?.ToArray();
        if (faceArray is null || faceArray.Length == 0)
        {
            faceArray = new Face[indexArray.Length / 3];
            for (var i = 0; i < faceArray.Length; i++)
                faceArray[i] = new Face { MaterialIndex = 0 };
        }

        var materialArray = materials?.ToArray();
        if (materialArray is null || materialArray.Length == 0)
            materialArray = [new MaterialData()];

        var usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr;
        var memory = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

        var vertexBytes = StructPacking.ToBytes<Vertex>(vertexArray);
        var indexBytes = StructPacking.ToBytes<uint>(indexArray);
        var faceBytes = StructPacking.ToBytes<Face>(faceArray);
        var materialBytes = StructPacking.ToBytes<MaterialData>(materialArray);

        var vertexBuffer = new GpuBuffer(context, (ulong)vertexBytes.Length, usage, memory, vertexBytes);
        var indexBuffer = new GpuBuffer(context, (ulong)indexBytes.Length, usage, memory, indexBytes);
        var faceBuffer = new GpuBuffer(context, (ulong)faceBytes.Length, usage, memory, faceBytes);
        var materialBuffer = new GpuBuffer(context, (ulong)materialBytes.Length, usage, memory, materialBytes);

        this.context = context;
        Name = name;
        VertexBuffer = vertexBuffer;
        IndexBuffer = indexBuffer;
        FaceBuffer = faceBuffer;
        MaterialBuffer = materialBuffer;

        // Compute traversal path uses a CPU-built BVH with GPU addresses.
        BvhBuilder.Build(vertexArray, indexArray, out var bvhNodes, out var bvhIndices);
        if (bvhNodes.Length > 0 && bvhIndices.Length > 0)
        {
            var bvhNodeBytes = StructPacking.ToBytes<BvhNodeGpu>(bvhNodes);
            var bvhIndexBytes = StructPacking.ToBytes<uint>(bvhIndices);
            bvhNodesBuffer = new GpuBuffer(
                context,
                (ulong)bvhNodeBytes.Length,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                bvhNodeBytes);
            bvhIndicesBuffer = new GpuBuffer(
                context,
                (ulong)bvhIndexBytes.Length,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                bvhIndexBytes);
        }

        // RTX path uses per-mesh BLAS.
        if (context.RayTracingSupported &&
            context.Api.TryGetDeviceExtension<KhrAccelerationStructure>(context.Instance, context.Device, out var accelExt))
        {
            var accel = new Accel(context, accelExt);
            accel.BuildBottomLevelTriangles(
                primitiveCount: (uint)faceArray.Length,
                vertexAddress: vertexBuffer.DeviceAddress,
                vertexStride: (ulong)System.Runtime.InteropServices.Marshal.SizeOf<Vertex>(),
                maxVertex: (uint)Math.Max(0, vertexArray.Length - 1),
                indexAddress: indexBuffer.DeviceAddress);
            blasRtx = accel;
        }

        Dirty = true;
    }

    public static MeshAsset Create(
        Context context,
        string name,
        IReadOnlyList<Vertex> vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<Face>? faces = null,
        IReadOnlyList<MaterialData>? materials = null)
    {
        return new MeshAsset(context, name, vertices, indices, faces, materials);
    }

    public static string BuildNameFromSourcePath(string meshPath, string fallbackName)
    {
        var baseName = string.IsNullOrWhiteSpace(fallbackName) ? "Mesh" : fallbackName;
        var cleanBase = new string(baseName.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        if (string.IsNullOrWhiteSpace(cleanBase))
            cleanBase = "Mesh";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(meshPath));
        var suffix = Convert.ToHexString(hash.AsSpan(0, 8));
        return $"{cleanBase}_{suffix}";
    }

    public static bool TryCreateFromUStaticMesh(
        Context context,
        UStaticMesh staticMesh,
        string meshAssetName,
        out MeshAsset? mesh,
        IReadOnlyList<MaterialData>? materials = null)
    {
        mesh = null;
        if (!staticMesh.TryConvert(out var converted))
            return false;

        using (converted)
        {
            var lod = converted.LODs.FirstOrDefault(x => !x.SkipLod && x.Verts is not null && x.Indices is not null);
            return lod is not null && TryCreateFromConvertedLod(context, meshAssetName, lod, out mesh, materials);
        }
    }

    public static bool TryCreateFromConvertedStaticMesh(
        Context context,
        CStaticMesh staticMesh,
        string meshAssetName,
        out MeshAsset? mesh,
        IReadOnlyList<MaterialData>? materials = null)
    {
        mesh = null;
        var lod = staticMesh.LODs.FirstOrDefault(x => !x.SkipLod && x.Verts is not null && x.Indices is not null);
        return lod is not null && TryCreateFromConvertedLod(context, meshAssetName, lod, out mesh, materials);
    }

    public static bool TryCreate(
        Context context,
        string name,
        IReadOnlyList<Vertex> vertices,
        IReadOnlyList<uint> indices,
        out MeshAsset? mesh,
        IReadOnlyList<Face>? faces = null,
        IReadOnlyList<MaterialData>? materials = null)
    {
        try
        {
            mesh = Create(context, name, vertices, indices, faces, materials);
            return true;
        }
        catch
        {
            mesh = null;
            return false;
        }
    }

    public static MeshAsset CreateCube(Context context, string name)
    {
        var h = 0.5f;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var faces = new List<Face>();

        var faceNormals = new[]
        {
            new Vector3(0, 0, 1), new Vector3(0, 0, -1),
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
            new Vector3(0, 1, 0), new Vector3(0, -1, 0)
        };

        var tangents = new[]
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
            new Vector3(0, 0, -1), new Vector3(0, 0, 1),
            new Vector3(1, 0, 0), new Vector3(1, 0, 0)
        };

        var bitangents = new[]
        {
            new Vector3(0, 1, 0), new Vector3(0, 1, 0),
            new Vector3(0, 1, 0), new Vector3(0, 1, 0),
            new Vector3(0, 0, -1), new Vector3(0, 0, 1)
        };

        uint vertexStart = 0;
        for (var faceIdx = 0; faceIdx < 6; faceIdx++)
        {
            var normal = faceNormals[faceIdx];
            var tangent = Vector3.Normalize(tangents[faceIdx]);
            var bitangent = Vector3.Normalize(bitangents[faceIdx]);

            var corners = new[]
            {
                normal * h + (-tangent - bitangent) * h,
                normal * h + (tangent - bitangent) * h,
                normal * h + (tangent + bitangent) * h,
                normal * h + (-tangent + bitangent) * h
            };

            var uvs = new[]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(1, 1),
                new Vector2(0, 1)
            };

            for (var i = 0; i < 4; i++)
            {
                vertices.Add(new Vertex
                {
                    Position = corners[i],
                    Normal = normal,
                    Tangent = tangent,
                    UV = uvs[i]
                });
            }

            indices.Add(vertexStart + 0);
            indices.Add(vertexStart + 1);
            indices.Add(vertexStart + 2);
            indices.Add(vertexStart + 0);
            indices.Add(vertexStart + 2);
            indices.Add(vertexStart + 3);

            faces.Add(new Face { MaterialIndex = 0 });
            faces.Add(new Face { MaterialIndex = 0 });

            vertexStart += 4;
        }

        return new MeshAsset(context, name, vertices, indices, faces, [new MaterialData()]);
    }

    public static MeshAsset CreateSphere(
        Context context,
        string name,
        uint latitudeSegments,
        uint longitudeSegments)
    {
        if (latitudeSegments < 2 || longitudeSegments < 3)
            throw new ArgumentException("Sphere segments too low. Use at least 2 latitude and 3 longitude segments.");

        const float radius = 0.5f;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var faces = new List<Face>();

        for (uint lat = 0; lat <= latitudeSegments; lat++)
        {
            var theta = MathF.PI * lat / latitudeSegments;
            var sinTheta = MathF.Sin(theta);
            var cosTheta = MathF.Cos(theta);

            for (uint lon = 0; lon <= longitudeSegments; lon++)
            {
                var phi = 2f * MathF.PI * lon / longitudeSegments;
                var sinPhi = MathF.Sin(phi);
                var cosPhi = MathF.Cos(phi);

                var normal = new Vector3(cosPhi * sinTheta, cosTheta, sinPhi * sinTheta);
                var position = normal * radius;
                var uv = new Vector2(lon / (float)longitudeSegments, lat / (float)latitudeSegments);

                Vector3 tangent;
                if (lat == 0)
                    tangent = new Vector3(1, 0, 0);
                else if (lat == latitudeSegments)
                    tangent = new Vector3(-1, 0, 0);
                else
                    tangent = Vector3.Normalize(new Vector3(-sinPhi, 0, cosPhi));

                vertices.Add(new Vertex
                {
                    Position = position,
                    Normal = normal,
                    Tangent = tangent,
                    UV = uv
                });
            }
        }

        for (uint lat = 0; lat < latitudeSegments; lat++)
        {
            for (uint lon = 0; lon < longitudeSegments; lon++)
            {
                var i0 = lat * (longitudeSegments + 1) + lon;
                var i1 = (lat + 1) * (longitudeSegments + 1) + lon;
                var i2 = i0 + 1;
                var i3 = i1 + 1;

                indices.Add(i0);
                indices.Add(i2);
                indices.Add(i1);
                indices.Add(i2);
                indices.Add(i3);
                indices.Add(i1);

                faces.Add(new Face { MaterialIndex = 0 });
                faces.Add(new Face { MaterialIndex = 0 });
            }
        }

        return new MeshAsset(context, name, vertices, indices, faces, [new MaterialData()]);
    }

    public static bool TryCreateCube(Context context, string name, out MeshAsset? mesh)
    {
        try
        {
            mesh = CreateCube(context, name);
            return true;
        }
        catch
        {
            mesh = null;
            return false;
        }
    }

    public static bool TryCreateSphere(
        Context context,
        string name,
        uint latitudeSegments,
        uint longitudeSegments,
        out MeshAsset? mesh)
    {
        try
        {
            mesh = CreateSphere(context, name, latitudeSegments, longitudeSegments);
            return true;
        }
        catch
        {
            mesh = null;
            return false;
        }
    }

    internal MeshAddressesGpu GetBufferAddresses()
    {
        return new MeshAddressesGpu
        {
            VertexAddress = VertexBuffer.DeviceAddress,
            IndexAddress = IndexBuffer.DeviceAddress,
            FaceAddress = FaceBuffer.DeviceAddress,
            MaterialAddress = MaterialBuffer.DeviceAddress,
            BvhNodeAddress = bvhNodesBuffer?.DeviceAddress ?? 0,
            BvhIndexAddress = bvhIndicesBuffer?.DeviceAddress ?? 0
        };
    }

    internal ulong GetBlasAddress()
    {
        return blasRtx?.GetDeviceAddress() ?? 0;
    }

    internal void ClearDirty() => Dirty = false;
    internal void MarkDirty() => Dirty = true;

    public void Dispose()
    {
        blasRtx?.Dispose();
        bvhIndicesBuffer?.Dispose();
        bvhNodesBuffer?.Dispose();
        MaterialBuffer.Dispose();
        FaceBuffer.Dispose();
        IndexBuffer.Dispose();
        VertexBuffer.Dispose();
    }

    private static Vector3 ConvertUnrealPosition(Vector3 unrealPosition)
    {
        var remapped = Vector3.TransformNormal(unrealPosition, UnrealToRendererBasis);
        return remapped * UnrealToRendererScale;
    }

    private static Vector3 ConvertUnrealDirection(Vector3 unrealDirection)
    {
        var remapped = Vector3.TransformNormal(unrealDirection, UnrealToRendererBasis);
        if (remapped.LengthSquared() <= 1e-12f)
            return Vector3.UnitZ;
        return Vector3.Normalize(remapped);
    }

    private static uint[] SanitizeIndices(
        IReadOnlyList<uint> indices,
        IReadOnlyList<Vertex> vertices,
        IReadOnlyList<Face> faces,
        bool flipWinding,
        out Face[] sanitizedFaces)
    {
        var triangleCount = indices.Count / 3;
        var sanitizedIndices = new List<uint>(indices.Count);
        var sanitizedFaceList = new List<Face>(triangleCount);

        for (var triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            var i0 = indices[(triangleIndex * 3) + 0];
            var i1 = indices[(triangleIndex * 3) + 1];
            var i2 = indices[(triangleIndex * 3) + 2];
            if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count)
                continue;

            if (i0 == i1 || i1 == i2 || i2 == i0)
                continue;

            if (IsDegenerateTriangle(vertices[(int)i0].Position, vertices[(int)i1].Position, vertices[(int)i2].Position))
                continue;

            sanitizedIndices.Add(i0);
            sanitizedIndices.Add(flipWinding ? i2 : i1);
            sanitizedIndices.Add(flipWinding ? i1 : i2);
            sanitizedFaceList.Add(triangleIndex < faces.Count ? faces[triangleIndex] : new Face { MaterialIndex = 0 });
        }

        sanitizedFaces = sanitizedFaceList.ToArray();
        return sanitizedIndices.ToArray();
    }

    private static bool HasFiniteVertices(IReadOnlyList<Vertex> vertices)
    {
        for (var i = 0; i < vertices.Count; i++)
        {
            var p = vertices[i].Position;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                return false;
        }

        return true;
    }

    private static bool TryCreateFromConvertedLod(
        Context context,
        string meshAssetName,
        CStaticMeshLod lod,
        out MeshAsset? mesh,
        IReadOnlyList<MaterialData>? materials = null)
    {
        mesh = null;
        if (lod.Verts is null || lod.Indices is null || lod.Verts.Length < 3)
            return false;

        var indices = lod.Indices.Value;
        if (indices.Length < 3)
            return false;

        var vertices = new Vertex[lod.Verts.Length];
        for (var i = 0; i < lod.Verts.Length; i++)
        {
            var src = lod.Verts[i];
            vertices[i] = new Vertex
            {
                Position = ConvertUnrealPosition(new Vector3(src.Position.X, src.Position.Y, src.Position.Z)),
                Normal = ConvertUnrealDirection(new Vector3(src.Normal.X, src.Normal.Y, src.Normal.Z)),
                Tangent = ConvertUnrealDirection(new Vector3(src.Tangent.X, src.Tangent.Y, src.Tangent.Z)),
                UV = new Vector2(src.UV.U, src.UV.V)
            };
        }

        if (!HasFiniteVertices(vertices))
            return false;

        var faces = new Face[indices.Length / 3];
        for (var i = 0; i < faces.Length; i++)
            faces[i] = new Face { MaterialIndex = 0 };

        if (lod.Sections is { } sectionsLazy)
        {
            foreach (var section in sectionsLazy.Value)
            {
                if (section.NumFaces <= 0)
                    continue;

                var materialIndex = Math.Max(0, section.MaterialIndex);
                var startFace = Math.Max(0, section.FirstIndex / 3);
                var endFace = Math.Min(faces.Length, startFace + section.NumFaces);
                for (var faceIndex = startFace; faceIndex < endFace; faceIndex++)
                    faces[faceIndex].MaterialIndex = materialIndex;
            }
        }

        var sanitizedIndices = SanitizeIndices(indices, vertices, faces, UnrealToRendererFlipsHandedness, out var sanitizedFaces);
        if (sanitizedIndices.Length < 3)
            return false;

        return TryCreate(context, meshAssetName, vertices, sanitizedIndices, out mesh, sanitizedFaces, materials);
    }

    private static bool IsDegenerateTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var area2 = Vector3.Cross(ab, ac).LengthSquared();
        return area2 <= 1e-12f;
    }
}
