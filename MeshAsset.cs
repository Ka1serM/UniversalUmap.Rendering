using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace UniversalUmap.Rendering;

public sealed class MeshAsset : IDisposable
{
    private readonly Context context;
    private readonly Accel? blasRtx;
    private readonly GpuBuffer? bvhNodesBuffer;
    private readonly GpuBuffer? bvhIndicesBuffer;

    public string Name { get; }
    public uint MeshIndex { get; internal set; } = uint.MaxValue;
    public IReadOnlyList<Vertex> Vertices { get; }
    public IReadOnlyList<uint> Indices { get; }
    public IReadOnlyList<Face> Faces { get; }
    public IReadOnlyList<MaterialData> Materials { get; }

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
        Vertices = vertexArray;
        Indices = indexArray;
        Faces = faceArray;
        Materials = materialArray;
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
}
