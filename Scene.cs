using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using CUE4Parse.UE4.Objects.Core.Math;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

public sealed class Scene : IDisposable, IScene
{
    public readonly record struct EnvironmentSnapshot(
        float Rotation,
        float VisibleExposure,
        float LightingExposure,
        bool Visible,
        int TextureIndex,
        Vector3 DirectionalDirection,
        float DirectionalIntensity);

    public readonly record struct CameraLensSnapshot(
        float FocalLengthMm,
        float Aperture,
        float FocusDistance,
        float BokehBias);

    public readonly record struct CameraViewSnapshot(
        Vector3 Position,
        Quaternion Rotation,
        Vector3 ArcballPivot);

    private readonly Context context;
    private readonly PerspectiveCamera activeCamera;
    private readonly List<SceneHierarchyNode> hierarchyRoots = [];
    private readonly object syncRoot = new();
    private readonly List<TextureAsset> textures = [];
    private readonly List<MeshAsset> meshAssets = [];
    private readonly List<MeshInstance> meshInstances = [];
    private readonly Dictionary<string, TextureAsset> texturesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeshAsset> meshAssetsByName = new(StringComparer.OrdinalIgnoreCase);
    private SceneDirtyFlags dirtyFlags;

    public ulong Version { get; private set; }
    public int SelectedInstanceIndex { get; private set; } = -1;
    public MeshInstance? SelectedInstance =>
        SelectedInstanceIndex >= 0 && SelectedInstanceIndex < meshInstances.Count
            ? meshInstances[SelectedInstanceIndex]
            : null;
    public IReadOnlyList<TextureAsset> Textures => textures;
    public IReadOnlyList<MeshAsset> MeshAssets => meshAssets;
    public IReadOnlyList<MeshInstance> MeshInstances => meshInstances;
    public IReadOnlyList<SceneHierarchyNode> HierarchyRoots => hierarchyRoots;
    internal EnvironmentDataGpu Environment { get; private set; } = new();
    internal CameraDataGpu Camera { get; private set; } = new();
    internal int CameraIsMoving { get; private set; }
    internal RenderMode RenderMode { get; private set; } = RenderMode.FullPathTracing;
    internal object SyncRoot => syncRoot;
    public Context Context => context;
    internal PerspectiveCamera ActiveCamera => activeCamera;
    internal PerspectiveCamera CameraController => activeCamera;
    internal event Action? CameraChanged;

    internal Scene(Context context, Input input)
    {
        this.context = context;
        activeCamera = new PerspectiveCamera(input);
    }

    private T Locked<T>(Func<T> action)
    {
        lock (syncRoot)
            return action();
    }

    private void Locked(Action action)
    {
        lock (syncRoot)
            action();
    }

    public void UpdateCamera(PixelSize renderSize, float deltaTimeSeconds)
    {
        Locked(() =>
        {
            var changed = activeCamera.Update(renderSize, deltaTimeSeconds, out var cameraData);
            SetCameraData(cameraData, changed);
        });
    }

    public TextureAsset Add(TextureAsset texture)
    {
        return Locked(() =>
        {
            if (texturesByName.ContainsKey(texture.Name))
                throw new InvalidOperationException($"Texture already exists: {texture.Name}");

            texture.Index = textures.Count;
            textures.Add(texture);
            texturesByName[texture.Name] = texture;
            Log.Information("Added texture '{TextureName}' at bindless index {TextureIndex}.", texture.Name, texture.Index);
            SetDirty(SceneDirtyFlags.Textures | SceneDirtyFlags.Accumulation);
            return texture;
        });
    }

    public TextureAsset? GetOrAddTexture(string name, Func<TextureAsset?> factory, out bool added)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Texture name is empty", nameof(name));
        if (factory is null)
            throw new ArgumentNullException(nameof(factory));

        lock (SyncRoot)
        {
            var existing = texturesByName.GetValueOrDefault(name);
            if (existing is not null)
            {
                added = false;
                return existing;
            }

            var created = factory();
            if (created is null)
            {
                added = false;
                return null;
            }

            existing = texturesByName.GetValueOrDefault(name);
            if (existing is not null)
            {
                created.Dispose();
                added = false;
                return existing;
            }

            added = true;
            return Add(created);
        }
    }

    public MeshAsset Add(MeshAsset mesh)
    {
        return Locked(() =>
        {
            if (meshAssetsByName.ContainsKey(mesh.Name))
                throw new InvalidOperationException($"Mesh already exists: {mesh.Name}");

            mesh.MeshIndex = (uint)meshAssets.Count;
            meshAssets.Add(mesh);
            meshAssetsByName[mesh.Name] = mesh;
            SetDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
            return mesh;
        });
    }

    public MeshAsset? GetOrAddMeshAsset(string name, Func<MeshAsset?> factory, out bool added)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Mesh name is empty", nameof(name));
        if (factory is null)
            throw new ArgumentNullException(nameof(factory));

        lock (SyncRoot)
        {
            var existing = meshAssetsByName.GetValueOrDefault(name);
            if (existing is not null)
            {
                added = false;
                return existing;
            }

            var created = factory();
            if (created is null)
            {
                added = false;
                return null;
            }

            existing = meshAssetsByName.GetValueOrDefault(name);
            if (existing is not null)
            {
                created.Dispose();
                added = false;
                return existing;
            }

            added = true;
            return Add(created);
        }
    }

    public MeshInstance Add(MeshInstance instance)
    {
        return Locked(() =>
        {
            if (string.IsNullOrWhiteSpace(instance.Name))
                throw new ArgumentException("Instance name is empty", nameof(instance));
            if (meshInstances.Exists(existing => existing.Name.Equals(instance.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Mesh instance already exists: {instance.Name}");
            if (!meshAssets.Contains(instance.MeshAsset))
                throw new InvalidOperationException($"Mesh asset '{instance.MeshAsset.Name}' must be added before creating instances.");

            meshInstances.Add(instance);
            SetDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
            return instance;
        });
    }

    public MeshAsset? FindMeshAsset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Locked(() => meshAssetsByName.GetValueOrDefault(name));
    }

    public TextureAsset? FindTexture(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Locked(() => texturesByName.GetValueOrDefault(name));
    }

    public MeshInstance CreateMeshInstance(MeshAsset mesh, string name, Matrix4x4 transform)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Instance name is empty", nameof(name));

        return Locked(() => Add(MeshInstance.Create(this, name, mesh, transform)));
    }

    public bool TryCreateMeshInstance(MeshAsset mesh, string name, FTransform unrealTransform, out MeshInstance? instance)
    {
        lock (SyncRoot)
        {
            instance = null;
            if (!MeshInstance.TryCreateFromUnrealTransform(this, name, mesh, unrealTransform, out var created) || created is null)
                return false;

            instance = Add(created);
            return true;
        }
    }

    public void SelectInstance(int instanceIndex)
    {
        Locked(() =>
        {
            if (instanceIndex < 0 || instanceIndex >= meshInstances.Count)
            {
                SelectedInstanceIndex = -1;
                return;
            }

            SelectedInstanceIndex = instanceIndex;
        });
    }

    public void ClearSelection()
    {
        Locked(() => SelectedInstanceIndex = -1);
    }

    public void SetEnvironment(TextureAsset texture, TextureAsset? cdfTexture = null)
    {
        Locked(() =>
        {
            if (texture.Index < 0)
                Add(texture);
            if (cdfTexture is not null && cdfTexture.Index < 0)
                Add(cdfTexture);

            var environment = Environment;
            environment.TextureIndex = texture.Index;
            environment.CdfTextureIndex = cdfTexture?.Index ?? -1;
            Environment = environment;
            SetDirty(SceneDirtyFlags.Accumulation);
        });
    }

    public void SetEnvironmentVisibleExposure(float exposureStops)
    {
        Locked(() =>
        {
            var environment = Environment;
            environment.VisibleExposure = exposureStops;
            Environment = environment;
            SetDirty(SceneDirtyFlags.Accumulation);
        });
    }

    public void SetEnvironmentLightingExposure(float exposureStops)
    {
        Locked(() =>
        {
            var environment = Environment;
            environment.LightingExposure = exposureStops;
            Environment = environment;
            SetDirty(SceneDirtyFlags.Accumulation);
        });
    }

    public void SetRenderMode(RenderMode renderMode)
    {
        Locked(() =>
        {
            if (RenderMode == renderMode)
                return;

            RenderMode = renderMode;
            Log.Information("Scene render mode changed to {RenderMode}.", renderMode);
            SetDirty(SceneDirtyFlags.Accumulation);
        });
    }

    internal void SetEnvironmentData(in EnvironmentDataGpu environment)
    {
        lock (SyncRoot)
        {
            Environment = environment;
            SetDirty(SceneDirtyFlags.Accumulation);
        }
    }

    public void TryLoadDefaultEnvironment()
    {
        const string hdriFileName = "golden_gate_hills_4k.hdr";
        var embeddedHdr = EmbeddedAssets.ReadByFileName(hdriFileName);

        try
        {
            var textureSet = TextureAsset.CreateHdrWithCdf(context, hdriFileName, embeddedHdr);
            Add(textureSet.Environment);
            Add(textureSet.Cdf);
            SetEnvironment(textureSet.Environment, textureSet.Cdf);
            Log.Information("Loaded default environment from embedded asset '{HdrFileName}'.", hdriFileName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load default environment map.");
        }
    }

    public bool IsDirty(SceneDirtyFlags flags) => Locked(() => (dirtyFlags & flags) != 0);

    public void ClearHierarchy()
    {
        Locked(() =>
        {
            hierarchyRoots.Clear();
            Version++;
        });
    }

    public void AddHierarchyRoot(SceneHierarchyNode root)
    {
        if (root is null)
            throw new ArgumentNullException(nameof(root));

        Locked(() =>
        {
            hierarchyRoots.Add(root);
            Version++;
        });
    }

    public void AddHierarchyChild(SceneHierarchyNode parent, SceneHierarchyNode child)
    {
        if (parent is null)
            throw new ArgumentNullException(nameof(parent));
        if (child is null)
            throw new ArgumentNullException(nameof(child));

        Locked(() =>
        {
            parent.Children.Add(child);
            Version++;
        });
    }

    public void SetHierarchyRoots(IReadOnlyList<SceneHierarchyNode> roots)
    {
        Locked(() =>
        {
            hierarchyRoots.Clear();
            hierarchyRoots.AddRange(roots);
            Version++;
        });
    }

    public void ClearGeometry(bool keepDefaultTestCube = false)
    {
        Locked(() =>
        {
            SelectedInstanceIndex = -1;

            if (keepDefaultTestCube)
            {
                meshInstances.Clear();
                SetDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
                return;
            }

            context.WaitForSubmittedCommandBuffers();
            meshInstances.Clear();

            foreach (var mesh in meshAssets)
                mesh.Dispose();

            meshAssets.Clear();
            meshAssetsByName.Clear();

            SetDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
        });
    }

    public void ClearDirty(SceneDirtyFlags flags)
    {
        Locked(() => dirtyFlags &= ~flags);
    }

    public IReadOnlyList<SceneHierarchyNode> GetHierarchyRootsSnapshot()
    {
        return Locked(() => hierarchyRoots.ToArray());
    }

    public EnvironmentSnapshot GetEnvironmentSnapshot()
    {
        return Locked(() => new EnvironmentSnapshot(
            Environment.Rotation,
            Environment.VisibleExposure,
            Environment.LightingExposure,
            Environment.Visible != 0,
            Environment.TextureIndex,
            Environment.DirectionalDirection,
            Environment.DirectionalIntensity));
    }

    public void ApplyEnvironmentSettings(EnvironmentSnapshot settings)
    {
        Locked(() =>
        {
            var environment = Environment;
            environment.Rotation = settings.Rotation;
            environment.VisibleExposure = settings.VisibleExposure;
            environment.LightingExposure = settings.LightingExposure;
            environment.Visible = settings.Visible ? 1 : 0;
            environment.TextureIndex = settings.TextureIndex;
            environment.DirectionalDirection = settings.DirectionalDirection;
            environment.DirectionalIntensity = settings.DirectionalIntensity;
            Environment = environment;
            SetDirty(SceneDirtyFlags.Accumulation);
        });
    }

    public CameraLensSnapshot GetCameraLensSnapshot()
    {
        return Locked(() => new CameraLensSnapshot(
            activeCamera.FocalLengthMm,
            activeCamera.Aperture,
            activeCamera.FocusDistance,
            activeCamera.BokehBias));
    }

    public void ApplyCameraLensSettings(CameraLensSnapshot settings)
    {
        Locked(() =>
        {
            activeCamera.FocalLengthMm = Math.Max(0.001f, settings.FocalLengthMm);
            activeCamera.Aperture = Math.Max(0f, settings.Aperture);
            activeCamera.FocusDistance = Math.Max(0.001f, settings.FocusDistance);
            activeCamera.BokehBias = Math.Max(0.001f, settings.BokehBias);
            SetDirty(SceneDirtyFlags.Accumulation);
        });
    }

    public CameraViewSnapshot GetCameraViewSnapshot()
    {
        return Locked(() => new CameraViewSnapshot(
            activeCamera.Position,
            activeCamera.Rotation,
            activeCamera.ArcballPivot));
    }

    public void SetArcballPivot(Vector3 pivot)
    {
        Locked(() => activeCamera.SetArcballPivot(pivot));
    }

    public void OrbitAroundPivot(float yawDelta, float pitchDelta)
    {
        Locked(() => activeCamera.OrbitAroundPivot(yawDelta, pitchDelta));
    }

    public void PanCameraInViewPlane(float deltaX, float deltaY)
    {
        Locked(() => activeCamera.PanInViewPlane(deltaX, deltaY));
    }

    public void DollyCamera(float amount)
    {
        Locked(() => activeCamera.Dolly(amount));
    }

    public void SetCameraView(Vector3 position, Quaternion rotation)
    {
        Locked(() =>
        {
            activeCamera.SetPosition(position);
            activeCamera.SetRotation(rotation);
        });
    }

    internal void SetDirty(SceneDirtyFlags flags)
    {
        dirtyFlags |= flags;
        Version++;
    }

    internal void SetCameraData(in CameraDataGpu camera, bool isMoving)
    {
        CameraIsMoving = isMoving ? 1 : 0;
        if (PerspectiveCamera.IsCameraDataEquivalent(Camera, camera))
            return;

        Camera = camera;
        SetDirty(SceneDirtyFlags.Accumulation);
        CameraChanged?.Invoke();
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
            instances[i] = meshInstances[i].BuildComputeInstanceData();

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
        // Ensure submitted GPU work is complete before releasing scene resources.
        context.WaitForSubmittedCommandBuffers();
        foreach (var texture in textures)
            texture.Dispose();
        foreach (var mesh in meshAssets)
            mesh.Dispose();

        meshInstances.Clear();
        meshAssets.Clear();
        textures.Clear();
        meshAssetsByName.Clear();
        texturesByName.Clear();
        hierarchyRoots.Clear();
        dirtyFlags = SceneDirtyFlags.None;
    }
}
