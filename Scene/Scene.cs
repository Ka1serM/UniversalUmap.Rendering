using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using Avalonia;
using CUE4Parse.UE4.Objects.Core.Math;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering;

public sealed class Scene : IDisposable, IScene
{
    private readonly Context context;
    private readonly List<SceneHierarchyNode> hierarchyRoots = [];
    private readonly object sync = new();
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
    public Camera Camera { get; }
    public EnvironmentSettings Environment { get; }
    public RenderSettings RenderSettings { get; }

    internal RenderMode RenderMode => RenderSettings.RenderMode;
    public Context Context => context;
    internal event Action? CameraChanged;
    internal event Action<EnvironmentSettings>? EnvironmentChanged;

    internal Scene(Context context, Input input)
    {
        this.context = context;
        Camera = new Camera(input ?? throw new ArgumentNullException(nameof(input)));
        Environment = new EnvironmentSettings();
        RenderSettings = new RenderSettings();
        Camera.Changed += OnCameraUpdated;
        Environment.PropertyChanged += OnEnvironmentUpdated;
        RenderSettings.PropertyChanged += OnRenderSettingsUpdated;
    }

    internal T Synchronize<T>(Func<T> action)
    {
        lock (sync)
            return action();
    }

    internal void Synchronize(Action action)
    {
        lock (sync)
            action();
    }

    private static string RequireNamedAsset<TAsset>(TAsset asset, Func<TAsset, string> getName, string assetType)
    {
        var name = getName(asset);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"{assetType} name is empty", nameof(asset));
        return name;
    }

    private TAsset AddNamedAsset<TAsset>(
        TAsset asset,
        Dictionary<string, TAsset> itemsByName,
        Func<TAsset, string> getName,
        Action<TAsset, int> initializeIndex,
        SceneDirtyFlags dirtyFlagsToSet,
        string assetType)
    {
        return Synchronize(() =>
        {
            var name = RequireNamedAsset(asset, getName, assetType);
            if (itemsByName.ContainsKey(name))
                throw new InvalidOperationException($"{assetType} already exists: {name}");

            initializeIndex(asset, itemsByName.Count);
            itemsByName[name] = asset;
            SetDirty(dirtyFlagsToSet);
            return asset;
        });
    }

    private bool ContainsMeshAsset(MeshAsset meshAsset)
    {
        return meshAssetsByName.TryGetValue(meshAsset.Name, out var existing) && ReferenceEquals(existing, meshAsset);
    }

    private TAsset? GetOrAddNamedAsset<TAsset>(
        string name,
        Func<TAsset?> factory,
        Dictionary<string, TAsset> itemsByName,
        Func<TAsset, TAsset> add,
        out bool added)
        where TAsset : class, IDisposable
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Asset name is empty", nameof(name));
        if (factory is null)
            throw new ArgumentNullException(nameof(factory));

        var result = Synchronize(() =>
        {
            if (itemsByName.TryGetValue(name, out var existing))
                return (existing, false);

            var created = factory();
            if (created is null)
                return ((TAsset?)null, false);

            if (itemsByName.TryGetValue(name, out existing))
            {
                created.Dispose();
                return (existing, false);
            }

            return (add(created), true);
        });

        added = result.Item2;
        return result.Item1;
    }

    private TAsset? FindNamedAsset<TAsset>(string name, Dictionary<string, TAsset> itemsByName)
        where TAsset : class
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return Synchronize(() => itemsByName.GetValueOrDefault(name));
    }

    public void UpdateCamera(PixelSize renderSize, float deltaTimeSeconds)
    {
        Synchronize(() =>
        {
            Camera.Update(renderSize, deltaTimeSeconds);
        });
    }

    public TextureAsset Add(TextureAsset texture)
    {
        var added = AddNamedAsset(
            texture,
            texturesByName,
            static item => item.Name,
            static (item, index) => item.Index = index,
            SceneDirtyFlags.Textures | SceneDirtyFlags.Accumulation,
            "Texture");
        Log.Information("Added texture '{TextureName}' at bindless index {TextureIndex}.", added.Name, added.Index);
        return added;
    }

    public TextureAsset? GetOrAddTexture(string name, Func<TextureAsset?> factory, out bool added)
    {
        return GetOrAddNamedAsset(name, factory, texturesByName, Add, out added);
    }

    public MeshAsset Add(MeshAsset mesh)
    {
        return AddNamedAsset(
            mesh,
            meshAssetsByName,
            static item => item.Name,
            static (item, index) => item.MeshIndex = (uint)index,
            SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation,
            "Mesh");
    }

    public MeshAsset? GetOrAddMeshAsset(string name, Func<MeshAsset?> factory, out bool added)
    {
        return GetOrAddNamedAsset(name, factory, meshAssetsByName, Add, out added);
    }

    public MeshInstance Add(MeshInstance instance)
    {
        return Synchronize(() =>
        {
            if (string.IsNullOrWhiteSpace(instance.Name))
                throw new ArgumentException("Instance name is empty", nameof(instance));
            if (meshInstances.Exists(existing => existing.Name.Equals(instance.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Mesh instance already exists: {instance.Name}");
            if (!ContainsMeshAsset(instance.MeshAsset))
                throw new InvalidOperationException($"Mesh asset '{instance.MeshAsset.Name}' must be added before creating instances.");

            meshInstances.Add(instance);
            SetDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
            return instance;
        });
    }

    public MeshAsset? FindMeshAsset(string name)
    {
        return FindNamedAsset(name, meshAssetsByName);
    }

    public TextureAsset? FindTexture(string name)
    {
        return FindNamedAsset(name, texturesByName);
    }

    public MeshInstance CreateMeshInstance(MeshAsset mesh, string name, Matrix4x4 transform)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Instance name is empty", nameof(name));

        return Synchronize(() => Add(MeshInstance.Create(this, name, mesh, transform)));
    }

    public bool TryCreateMeshInstance(MeshAsset mesh, string name, FTransform unrealTransform, out MeshInstance? instance)
    {
        instance = Synchronize(() =>
        {
            if (!MeshInstance.TryCreateFromUnrealTransform(this, name, mesh, unrealTransform, out var created) || created is null)
                return null;

            return Add(created);
        });
        return instance is not null;
    }

    public void SelectInstance(int instanceIndex)
    {
        Synchronize(() =>
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
        Synchronize(() => SelectedInstanceIndex = -1);
    }

    public void SetEnvironment(TextureAsset texture, TextureAsset? cdfTexture = null)
    {
        Synchronize(() =>
        {
            if (texture.Index < 0)
                Add(texture);
            if (cdfTexture is not null && cdfTexture.Index < 0)
                Add(cdfTexture);

            Environment.TextureIndex = texture.Index;
            Environment.CdfTextureIndex = cdfTexture?.Index ?? -1;
        });
    }

    public void SetRenderMode(RenderMode renderMode)
    {
        Synchronize(() => RenderSettings.RenderMode = renderMode);
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

    public bool IsDirty(SceneDirtyFlags flags) => Synchronize(() => (dirtyFlags & flags) != 0);

    public void ClearHierarchy()
    {
        Synchronize(() =>
        {
            hierarchyRoots.Clear();
            Version++;
        });
    }

    public void AddHierarchyRoot(SceneHierarchyNode root)
    {
        if (root is null)
            throw new ArgumentNullException(nameof(root));

        Synchronize(() =>
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

        Synchronize(() =>
        {
            parent.Children.Add(child);
            Version++;
        });
    }

    public void SetHierarchyRoots(IReadOnlyList<SceneHierarchyNode> roots)
    {
        Synchronize(() =>
        {
            hierarchyRoots.Clear();
            hierarchyRoots.AddRange(roots);
            Version++;
        });
    }

    public void ClearGeometry(bool keepDefaultTestCube = false)
    {
        Synchronize(() =>
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

            foreach (var mesh in meshAssetsByName.Values)
                mesh.Dispose();

            meshAssetsByName.Clear();

            SetDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
        });
    }

    public void ClearDirty(SceneDirtyFlags flags)
    {
        Synchronize(() => dirtyFlags &= ~flags);
    }

    public IReadOnlyList<SceneHierarchyNode> GetHierarchyRootsSnapshot()
    {
        return Synchronize(() => hierarchyRoots.ToArray());
    }

    internal TextureAsset[] GetTexturesSnapshot()
    {
        return Synchronize(() => texturesByName.Values.ToArray());
    }

    internal bool TryGetTextureAt(int index, out TextureAsset? texture)
    {
        var result = Synchronize(() =>
        {
            if (index < 0 || index >= texturesByName.Count)
                return ((TextureAsset?)null, false);

            return (texturesByName.Values.ElementAt(index), true);
        });

        texture = result.Item1;
        return result.Item2;
    }

    internal bool TrySelectInstance(uint instanceId, out MeshInstance? instance)
    {
        var result = Synchronize(() =>
        {
            if (instanceId == SharedShaderDefines.InvalidInstance || instanceId >= meshInstances.Count)
            {
                SelectedInstanceIndex = -1;
                return ((MeshInstance?)null, false);
            }

            SelectedInstanceIndex = (int)instanceId;
            return (meshInstances[SelectedInstanceIndex], true);
        });

        instance = result.Item1;
        return result.Item2;
    }

    public void SetArcballPivot(Vector3 pivot)
    {
        Synchronize(() => Camera.SetArcballPivot(pivot));
    }

    public void OrbitAroundPivot(float yawDelta, float pitchDelta)
    {
        Synchronize(() => Camera.OrbitAroundPivot(yawDelta, pitchDelta));
    }

    public void PanCameraInViewPlane(float deltaX, float deltaY)
    {
        Synchronize(() => Camera.PanInViewPlane(deltaX, deltaY));
    }

    public void DollyCamera(float amount)
    {
        Synchronize(() => Camera.Dolly(amount));
    }

    public void SetCameraView(Vector3 position, Quaternion rotation)
    {
        Synchronize(() =>
        {
            Camera.SetPosition(position);
            Camera.SetRotation(rotation);
        });
    }

    internal void SetDirty(SceneDirtyFlags flags)
    {
        dirtyFlags |= flags;
        Version++;
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
        if (meshAssetsByName.Count == 0)
        {
            var empty = new MeshAddressesGpu();
            return StructPacking.ToBytes(new[] { empty });
        }

        var addresses = new MeshAddressesGpu[meshAssetsByName.Count];
        var index = 0;
        foreach (var meshAsset in meshAssetsByName.Values)
            addresses[index++] = meshAsset.GetBufferAddresses();

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
        Camera.Changed -= OnCameraUpdated;
        Environment.PropertyChanged -= OnEnvironmentUpdated;
        RenderSettings.PropertyChanged -= OnRenderSettingsUpdated;
        foreach (var texture in texturesByName.Values)
            texture.Dispose();
        foreach (var mesh in meshAssetsByName.Values)
            mesh.Dispose();

        meshInstances.Clear();
        meshAssetsByName.Clear();
        texturesByName.Clear();
        hierarchyRoots.Clear();
        dirtyFlags = SceneDirtyFlags.None;
    }

    public CameraViewSnapshot GetCameraViewSnapshot()
    {
        return Synchronize(() => new CameraViewSnapshot(
            Camera.Position,
            Camera.Rotation,
            Camera.ArcballPivot));
    }

    public readonly record struct CameraViewSnapshot(
        Vector3 Position,
        Quaternion Rotation,
        Vector3 ArcballPivot);

    private void OnCameraUpdated()
    {
        SetDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
        CameraChanged?.Invoke();
    }

    private void OnEnvironmentUpdated(object? sender, PropertyChangedEventArgs e)
    {
        SetDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
        EnvironmentChanged?.Invoke(Environment);
    }

    private void OnRenderSettingsUpdated(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RenderSettings.RenderMode))
        {
            Log.Information("Scene render mode changed to {RenderMode}.", RenderSettings.RenderMode);
            SetDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
            return;
        }

        SetDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
    }
}
