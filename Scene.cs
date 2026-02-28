using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

public sealed class Scene : IDisposable, IScene
{
    private const string DefaultCubeMeshName = "DefaultCube";
    private const string DefaultCubeInstanceName = "DefaultCubeInstance";
    private readonly Context context;
    private readonly List<TextureAsset> textures = [];
    private readonly List<MeshAsset> meshAssets = [];
    private readonly List<MeshInstance> meshInstances = [];
    private SceneDirtyFlags dirtyFlags;
    private const float CameraEpsilon = 0.0001f;

    public ulong Version { get; private set; }

    public IReadOnlyList<TextureAsset> Textures => textures;
    public IReadOnlyList<MeshAsset> MeshAssets => meshAssets;
    public IReadOnlyList<MeshInstance> MeshInstances => meshInstances;
    internal EnvironmentDataGpu Environment { get; private set; } = new()
    {
        Color = Vector3.One,
        Intensity = 1f,
        TextureIndex = -1,
        CdfTextureIndex = -1,
        Rotation = 0f,
        Exposure = 2f,
        Visible = 1
    };
    internal CameraDataGpu Camera { get; private set; } = new()
    {
        Position = new Vector3(0f, 0f, -2f),
        Direction = new Vector3(0f, 0f, 1f),
        Horizontal = new Vector3(1f, 0f, 0f),
        Vertical = new Vector3(0f, 1f, 0f),
        FocalLength = 50f,
        FocusDistance = 4f,
        Aperture = 0f,
        BokehBias = 1f
    };
    internal int CameraIsMoving { get; private set; }

    public Scene(Context context)
    {
        this.context = context;
        TryCreateDefaultCube();
    }

    public TextureAsset Add(TextureAsset texture)
    {
        if (textures.Any(x => string.Equals(x.Name, texture.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Texture already exists: {texture.Name}");

        texture.Index = textures.Count;
        textures.Add(texture);
        Log.Information("Added texture '{TextureName}' at bindless index {TextureIndex}.", texture.Name, texture.Index);
        SetDirty(SceneDirtyFlags.Textures | SceneDirtyFlags.Accumulation);
        return texture;
    }

    public MeshAsset Add(MeshAsset mesh)
    {
        if (meshAssets.Any(x => string.Equals(x.Name, mesh.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Mesh already exists: {mesh.Name}");

        mesh.MeshIndex = (uint)meshAssets.Count;
        meshAssets.Add(mesh);
        SetDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
        return mesh;
    }

    public MeshInstance Add(MeshInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Name))
            throw new ArgumentException("Instance name is empty", nameof(instance));
        if (meshInstances.Any(x => string.Equals(x.Name, instance.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Mesh instance already exists: {instance.Name}");
        if (!meshAssets.Contains(instance.MeshAsset))
            throw new InvalidOperationException($"Mesh asset '{instance.MeshAsset.Name}' must be added before creating instances.");

        meshInstances.Add(instance);
        SetDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
        return instance;
    }

    public MeshInstance CreateMeshInstance(MeshAsset mesh, string name, Matrix4x4 transform)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Instance name is empty", nameof(name));

        return Add(MeshInstance.Create(this, name, mesh, transform));
    }

    public void SetEnvironment(TextureAsset texture)
    {
        if (texture.Index < 0)
            Add(texture);

        var environment = Environment;
        environment.TextureIndex = texture.Index;
        Environment = environment;
        SetDirty(SceneDirtyFlags.Accumulation);
    }


    public MeshAsset CreateDefaultCube()
    {
        var mesh = MeshAsset.CreateCube(context, "DefaultCube");

        try
        {
            Add(mesh);
            CreateMeshInstance(mesh, "DefaultCubeInstance", Matrix4x4.Identity);
            return mesh;
        }
        catch
        {
            mesh.Dispose();
            throw;
        }
    }

    private void TryCreateDefaultCube()
    {
        try
        {
            CreateDefaultCube();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create default cube test mesh.");
        }
    }

    public bool IsDirty(SceneDirtyFlags flags) => (dirtyFlags & flags) != 0;

    public void ClearGeometry(bool keepDefaultTestCube = false)
    {
        if (!keepDefaultTestCube)
        {
            foreach (var mesh in meshAssets)
                mesh.Dispose();

            meshInstances.Clear();
            meshAssets.Clear();
            SetDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
            return;
        }

        for (var i = meshInstances.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(meshInstances[i].Name, DefaultCubeInstanceName, StringComparison.Ordinal))
                meshInstances.RemoveAt(i);
        }

        for (var i = meshAssets.Count - 1; i >= 0; i--)
        {
            if (string.Equals(meshAssets[i].Name, DefaultCubeMeshName, StringComparison.Ordinal))
                continue;

            meshAssets[i].Dispose();
            meshAssets.RemoveAt(i);
        }

        SetDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
    }

    public void ClearDirty(SceneDirtyFlags flags)
    {
        dirtyFlags &= ~flags;
    }

    internal void SetDirty(SceneDirtyFlags flags)
    {
        dirtyFlags |= flags;
        Version++;
    }

    internal void SetCameraData(in CameraDataGpu camera, bool isMoving)
    {
        CameraIsMoving = isMoving ? 1 : 0;
        if (CameraEquals(Camera, camera))
            return;

        Camera = camera;
        SetDirty(SceneDirtyFlags.Accumulation);
    }

    void IScene.SetAccumulationDirty()
    {
        SetDirty(SceneDirtyFlags.Accumulation);
    }

    void IScene.SetTlasDirty()
    {
        SetDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
    }

    internal byte[] BuildComputeInstanceData()
    {
        if (meshInstances.Count == 0)
        {
            var empty = new ComputeInstanceGpu
            {
                Transform = Matrix4x4.Identity,
                InverseTransform = Matrix4x4.Identity,
                MeshId = uint.MaxValue
            };
            return StructPacking.ToBytes(new[] { empty });
        }

        var instances = new ComputeInstanceGpu[meshInstances.Count];
        for (var i = 0; i < meshInstances.Count; i++)
        {
            var transform = meshInstances[i].Transform;
            Matrix4x4.Invert(transform, out var inverse);
            instances[i] = new ComputeInstanceGpu
            {
                Transform = transform,
                InverseTransform = inverse,
                MeshId = meshInstances[i].MeshAsset.MeshIndex
            };
        }

        if (meshInstances.Count > 0)
        {
            var first = meshInstances[0].Transform;
            Log.Debug(
                "Compute instance sample: count={Count}, firstGpuCol3=({C14:0.###},{C24:0.###},{C34:0.###}), firstGpuRow4=({R41:0.###},{R42:0.###},{R43:0.###})",
                meshInstances.Count,
                first.M14,
                first.M24,
                first.M34,
                first.M41,
                first.M42,
                first.M43);
        }

        return StructPacking.ToBytes(instances);
    }

    internal byte[] BuildMeshAddressData()
    {
        if (meshAssets.Count == 0)
        {
            var empty = new MeshAddressesGpu();
            return StructPacking.ToBytes(new[] { empty });
        }

        var addresses = new MeshAddressesGpu[meshAssets.Count];
        for (var i = 0; i < meshAssets.Count; i++)
            addresses[i] = meshAssets[i].GetBufferAddresses();

        return StructPacking.ToBytes(addresses);
    }

    internal byte[] BuildRtxInstanceData()
    {
        if (meshInstances.Count == 0)
        {
            var empty = new AccelerationStructureInstanceKHR();
            return StructPacking.ToBytes(new[] { empty });
        }

        var instances = new AccelerationStructureInstanceKHR[meshInstances.Count];
        for (var i = 0; i < meshInstances.Count; i++)
            instances[i] = meshInstances[i].BuildRtxInstanceData();

        return StructPacking.ToBytes(instances);
    }

    public void Dispose()
    {
        foreach (var texture in textures)
            texture.Dispose();
        foreach (var mesh in meshAssets)
            mesh.Dispose();

        meshInstances.Clear();
        meshAssets.Clear();
        textures.Clear();
        dirtyFlags = SceneDirtyFlags.None;
    }

    private static bool CameraEquals(in CameraDataGpu a, in CameraDataGpu b)
    {
        return Vector3.DistanceSquared(a.Position, b.Position) <= CameraEpsilon * CameraEpsilon &&
               Vector3.DistanceSquared(a.Direction, b.Direction) <= CameraEpsilon * CameraEpsilon &&
               Vector3.DistanceSquared(a.Horizontal, b.Horizontal) <= CameraEpsilon * CameraEpsilon &&
               Vector3.DistanceSquared(a.Vertical, b.Vertical) <= CameraEpsilon * CameraEpsilon &&
               MathF.Abs(a.FocalLength - b.FocalLength) <= CameraEpsilon &&
               MathF.Abs(a.FocusDistance - b.FocusDistance) <= CameraEpsilon &&
               MathF.Abs(a.Aperture - b.Aperture) <= CameraEpsilon &&
               MathF.Abs(a.BokehBias - b.BokehBias) <= CameraEpsilon;
    }
}
