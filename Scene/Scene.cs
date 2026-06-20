using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Avalonia;
using CUE4Parse.UE4.Objects.Core.Math;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Inspector;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering;

public sealed class Scene : IDisposable, IScene
{
    private const int PackedEnvironmentMagic = 0x504D4555;
    private const int PackedEnvironmentMipChainMagic = 0x324D4555;
    private const int PackedEnvironmentHeaderByteLength = 16;
    private const int PackedEnvironmentMipChainHeaderByteLength = 20;
    private const ushort HalfFloatAlphaOne = 0x3C00;

    private sealed class DeferredUpdateScope : IDisposable
    {
        private readonly Scene owner;
        private bool disposed;

        public DeferredUpdateScope(Scene owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            owner.EndDeferredUpdates();
        }
    }

    private readonly Context context;
    private readonly Input input;
    private readonly List<SceneHierarchyNode> hierarchyRoots = [];
    private readonly Dictionary<int, SceneHierarchyNode> hierarchyById = [];
    private readonly object sync = new();
    private readonly List<MeshInstance> meshInstances = [];
    private readonly Dictionary<string, TextureAsset> texturesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeshAsset> meshAssetsByName = new(StringComparer.OrdinalIgnoreCase);
    private SceneDirtyFlags dirtyFlags;
    private SceneDirtyFlags deferredDirtyFlags;
    private int deferredUpdateDepth;
    private bool deferredHierarchyChanged;
    private ulong meshesRevision;
    private ulong tlasRevision;
    private ulong texturesRevision;
    private ulong settingsRevision;
    private int nextHierarchyNodeId;
    private Func<Func<object?>, object?>? gpuDispatcher;
    public int SelectedInstanceIndex { get; private set; } = -1;
    public int SelectedHierarchyNodeId { get; private set; } = -1;
    public MeshInstance? SelectedInstance =>
        SelectedInstanceIndex >= 0 && SelectedInstanceIndex < meshInstances.Count
            ? meshInstances[SelectedInstanceIndex]
            : null;
    public event Action<int>? SelectedInstanceChanged;
    public event Action<int>? SelectedHierarchyNodeChanged;
    public event Action? HierarchyChanged;
    public CameraBase Camera { get; private set; }
    public EnvironmentSettings Environment { get; }
    public RenderSettings RenderSettings { get; }

    internal RenderMode RenderMode => RenderSettings.RenderMode;
    public Context Context => context;
    internal event Action? CameraChanged;
    internal event Action<EnvironmentSettings>? EnvironmentChanged;
    internal event Action<SceneDirtyFlags>? DirtyStateChanged;

    internal readonly record struct RenderDataGpu(
        BufferVisualizationMode BufferVisualization,
        uint SelectedInstanceId,
        int EnvironmentTextureIndex,
        int IsMoving,
        RenderSettingsDataGpu RenderSettings,
        CameraDataGpu Camera,
        EnvironmentDataGpu Environment);

    internal readonly record struct ResourceRevisions(
        ulong Meshes,
        ulong Tlas,
        ulong Textures,
        ulong Settings);

    internal Scene(Context context, Input input)
    {
        this.context = context;
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        Camera = new PerspectiveCamera(this.input, new CameraSettings());
        Environment = new EnvironmentSettings();
        RenderSettings = new RenderSettings();
        Camera.Changed += OnCameraUpdated;
        Camera.ProjectionChangeRequested += OnCameraProjectionChangeRequested;
        Environment.PropertyChanged += OnEnvironmentUpdated;
        RenderSettings.PropertyChanged += OnRenderSettingsUpdated;
    }

    public IInspectable? ResolveSelectedInspectable()
    {
        return Synchronize<IInspectable?>(() =>
        {
            if (SelectedInstance is { } instance)
                return instance;
            return null;
        });
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

    internal void SetGpuDispatcher(Func<Func<object?>, object?> dispatcher)
    {
        gpuDispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public T RunOnGpuThread<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = gpuDispatcher;
        if (dispatcher is null)
            return action();

        var result = dispatcher(() => action());
        return result is T typed ? typed : default!;
    }

    public void RunOnGpuThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunOnGpuThread<object?>(() =>
        {
            action();
            return null;
        });
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

        TAsset? result;
        lock (sync)
        {
            if (itemsByName.TryGetValue(name, out var existing))
            {
                added = false;
                return existing;
            }
        }

        var created = factory();
        if (created is null)
        {
            added = false;
            return null;
        }

        lock (sync)
        {
            if (itemsByName.TryGetValue(name, out var existing))
            {
                created.Dispose();
                added = false;
                return existing;
            }

            result = add(created);
        }

        added = true;
        return result;
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

    public void UpdateCameraRenderSize(PixelSize renderSize)
    {
        Synchronize(() =>
        {
            Camera.UpdateRenderSize(renderSize);
        });
    }

    internal RenderDataGpu CaptureRenderData()
    {
        return Synchronize(() => new RenderDataGpu(
            RenderSettings.BufferVisualization,
            SelectedInstanceIndex >= 0 ? (uint)SelectedInstanceIndex : ShaderDefines.INVALID_INSTANCE,
            Environment.TextureIndex,
            Camera.IsMoving,
            RenderSettings.ToStruct(),
            Camera.ToStruct(),
            Environment.ToStruct()));
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
        Log.Debug("Added texture '{TextureName}' at bindless index {TextureIndex}.", added.Name, added.Index);
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

            instance.SetOwner(this);
            meshInstances.Add(instance);
            SetDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
            return instance;
        });
    }

    public MeshAsset? FindMeshAsset(string name)
    {
        return FindNamedAsset(name, meshAssetsByName);
    }

    public int GetInstanceCount()
    {
        return Synchronize(() => meshInstances.Count);
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
        return TryCreateMeshInstance(mesh, name, unrealTransform, SceneHierarchyHandle.Invalid, out instance);
    }

    public bool TryCreateMeshInstance(MeshAsset mesh, string name, FTransform unrealTransform, SceneHierarchyHandle ownerNode, out MeshInstance? instance)
    {
        instance = Synchronize(() =>
        {
            if (!MeshInstance.TryCreateFromUnrealTransform(this, name, mesh, unrealTransform, out var created, ownerNode.Id) || created is null)
                return null;

            return Add(created);
        });
        return instance is not null;
    }

    public void SelectInstance(int instanceIndex)
    {
        var result = Synchronize(() =>
        {
            var changedInstance = TrySetSelectedInstanceIndexUnsafe(instanceIndex);
            var selectedHierarchyNodeId = SelectedInstanceIndex >= 0
                ? meshInstances[SelectedInstanceIndex].HierarchyNodeId
                : -1;
            var changedNode = TrySetSelectedHierarchyNodeIdUnsafe(selectedHierarchyNodeId);

            return (SelectedInstanceIndex, SelectedHierarchyNodeId, changedInstance, changedNode);
        });

        if (result.changedInstance)
            SelectedInstanceChanged?.Invoke(result.SelectedInstanceIndex);
        if (result.changedNode)
            SelectedHierarchyNodeChanged?.Invoke(result.SelectedHierarchyNodeId);
    }

    public void ClearSelection()
    {
        var result = Synchronize(() =>
        {
            var changedInstance = TrySetSelectedInstanceIndexUnsafe(-1);
            var changedNode = TrySetSelectedHierarchyNodeIdUnsafe(-1);
            return (changedInstance, changedNode);
        });

        if (result.changedInstance)
            SelectedInstanceChanged?.Invoke(-1);
        if (result.changedNode)
            SelectedHierarchyNodeChanged?.Invoke(-1);
    }

    public bool SelectHierarchyNode(SceneHierarchyHandle handle)
    {
        var result = Synchronize(() =>
        {
            if (!handle.IsValid || !hierarchyById.TryGetValue(handle.Id, out var node))
            {
                var changedNode = TrySetSelectedHierarchyNodeIdUnsafe(-1);
                var changedInstance = TrySetSelectedInstanceIndexUnsafe(-1);
                return (
                    selectedInstanceIndex: -1,
                    selectedHierarchyNodeId: -1,
                    changedInstanceSelection: changedInstance,
                    changedNodeSelection: changedNode);
            }

            var instanceIndex = node.FirstInstanceIndexOrDefault();
            var changedNodeSelection = TrySetSelectedHierarchyNodeIdUnsafe(handle.Id);
            var changedInstanceSelection = TrySetSelectedInstanceIndexUnsafe(instanceIndex);

            return (
                selectedInstanceIndex: SelectedInstanceIndex,
                selectedHierarchyNodeId: SelectedHierarchyNodeId,
                changedInstanceSelection,
                changedNodeSelection);
        });

        if (result.changedInstanceSelection)
            SelectedInstanceChanged?.Invoke(result.selectedInstanceIndex);
        if (result.changedNodeSelection)
            SelectedHierarchyNodeChanged?.Invoke(result.selectedHierarchyNodeId);

        return result.selectedHierarchyNodeId >= 0;
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
            Environment.MaxTextureLod = Math.Max(texture.Image.MipLevels - 1u, 0u);
        });
    }

    public void SetRenderMode(RenderMode renderMode)
    {
        Synchronize(() => RenderSettings.RenderMode = renderMode);
    }

    public IDisposable DeferUpdates()
    {
        Synchronize(() => deferredUpdateDepth++);
        return new DeferredUpdateScope(this);
    }

    public void LoadDebugScene()
    {
        using var deferredUpdates = DeferUpdates();

        var redMaterial = new MaterialData { Albedo = new Vector3(0.9f, 0.15f, 0.1f), Roughness = 0.4f };
        var blueMaterial = new MaterialData { Albedo = new Vector3(0.1f, 0.3f, 0.9f), Roughness = 0.15f, Metallic = 1f };

        var debugRoot = AddHierarchyRoot(
            "Debug Scene",
            "DebugScene",
            string.Empty,
            "debug://scene",
            null,
            SceneNodeRole.World,
            SceneHierarchyNodeKind.World,
            FTransform.Identity,
            FTransform.Identity,
            instanceCount: 2);
        var cubeNode = AddHierarchyChild(
            debugRoot,
            "Debug Cube",
            "StaticMeshComponent",
            string.Empty,
            "debug://scene/cube",
            "debug://mesh/cube",
            SceneNodeRole.Component,
            SceneHierarchyNodeKind.StaticMesh,
            FTransform.Identity,
            FTransform.Identity);
        var sphereNode = AddHierarchyChild(
            debugRoot,
            "Debug Sphere",
            "StaticMeshComponent",
            string.Empty,
            "debug://scene/sphere",
            "debug://mesh/sphere",
            SceneNodeRole.Component,
            SceneHierarchyNodeKind.StaticMesh,
            FTransform.Identity,
            FTransform.Identity);

        var cube = MeshAsset.CreateCube(context, "DebugCube", redMaterial);
        var sphere = MeshAsset.CreateSphere(context, "DebugSphere", 32, 32, blueMaterial);
        Add(cube);
        Add(sphere);

        var sphereInstanceStart = GetInstanceCount();
        Add(MeshInstance.Create(this, "DebugSphereInstance", sphere, Matrix4x4.CreateTranslation(0.75f, 0f, 0f), sphereNode.Id));
        AddInstanceRange(sphereNode, sphereInstanceStart, 1);
    }

    public void TryLoadDefaultEnvironment()
    {
        const string hdriName = "default_hdri";

        try
        {
            var environment = CreateEnvironmentMapTexture(hdriName, $"Assets/Precomputed/{hdriName}_env.bin", generateMipmaps: true);
            var cdf = CreateEnvironmentMapTexture($"{hdriName}_cdf", $"Assets/Precomputed/{hdriName}_cdf.bin");

            Add(environment);
            Add(cdf);
            SetEnvironment(environment, cdf);
            Log.Information("Loaded precompiled default environment '{HdrName}'.", hdriName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load precompiled default environment map '{HdrName}'.", hdriName);
        }
    }

    private TextureAsset CreateEnvironmentMapTexture(string textureName, string embeddedFileName, bool generateMipmaps = false)
    {
        var packed = EmbeddedAssets.ReadByFileName(embeddedFileName);
        if (packed.Length < 8)
            throw new InvalidDataException($"Invalid environment precompile payload: {embeddedFileName}");

        var magic = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(0, 4));
        if (magic == PackedEnvironmentMagic || magic == PackedEnvironmentMipChainMagic)
            return CreateEnvironmentMapTextureFromTypedPayload(textureName, embeddedFileName, packed, generateMipmaps);

        var width = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(0, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(4, 4));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Invalid environment precompile payload: {embeddedFileName}");
        var expectedByteLength = checked(width * height * 4 * sizeof(float));
        if (packed.Length != 8 + expectedByteLength)
            throw new InvalidDataException($"Invalid environment precompile payload: {embeddedFileName}");

        var rgba32fBytes = packed.AsSpan(8).ToArray();

        return TextureAsset.CreateRgba32Float(
            context,
            textureName,
            string.Empty,
            rgba32fBytes,
            (uint)width,
            (uint)height,
            generateMipmaps);
    }

    private TextureAsset CreateEnvironmentMapTextureFromTypedPayload(
        string textureName,
        string embeddedFileName,
        byte[] packed,
        bool generateMipmaps)
    {
        if (packed.Length < PackedEnvironmentHeaderByteLength)
            throw new InvalidDataException($"Invalid environment precompile payload: {embeddedFileName}");

        var magic = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(0, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(4, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(8, 4));
        var format = (PackedEnvironmentTextureFormat)BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(12, 4));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Invalid environment precompile payload: {embeddedFileName}");

        if (magic == PackedEnvironmentMipChainMagic)
        {
            if (packed.Length < PackedEnvironmentMipChainHeaderByteLength)
                throw new InvalidDataException($"Invalid mip-chain environment precompile payload: {embeddedFileName}");

            var mipCount = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(16, 4));
            if (mipCount <= 0)
                throw new InvalidDataException($"Invalid mip count in environment precompile payload: {embeddedFileName}");

            return format switch
            {
                PackedEnvironmentTextureFormat.Rgb16Float => CreateRgb16EnvironmentTextureMipChain(
                    textureName,
                    embeddedFileName,
                    packed,
                    width,
                    height,
                    mipCount),
                PackedEnvironmentTextureFormat.Rgba16Float => CreateRgba16EnvironmentTextureMipChain(
                    textureName,
                    embeddedFileName,
                    packed,
                    width,
                    height,
                    mipCount),
                _ => throw new InvalidDataException($"Unsupported environment precompile payload format {format}: {embeddedFileName}")
            };
        }

        return format switch
        {
            PackedEnvironmentTextureFormat.Rgb16Float => CreateRgb16EnvironmentTexture(
                textureName,
                embeddedFileName,
                packed,
                width,
                height,
                generateMipmaps),
            PackedEnvironmentTextureFormat.Rgba16Float => CreateRgba16EnvironmentTexture(
                textureName,
                embeddedFileName,
                packed,
                width,
                height,
                generateMipmaps),
            _ => throw new InvalidDataException($"Unsupported environment precompile payload format {format}: {embeddedFileName}")
        };
    }

    private static int CalculatePackedMipByteLength(int width, int height, int channelCount, int mipCount)
    {
        var total = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var level = 0; level < mipCount; level++)
        {
            total = checked(total + mipWidth * mipHeight * channelCount * sizeof(ushort));
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        return total;
    }

    private TextureAsset CreateRgb16EnvironmentTexture(
        string textureName,
        string embeddedFileName,
        byte[] packed,
        int width,
        int height,
        bool generateMipmaps)
    {
        var expectedByteLength = checked(width * height * 3 * sizeof(ushort));
        if (packed.Length != PackedEnvironmentHeaderByteLength + expectedByteLength)
            throw new InvalidDataException($"Invalid RGB16F environment precompile payload: {embeddedFileName}");

        var source = packed.AsSpan(PackedEnvironmentHeaderByteLength);
        var rgba16fBytes = new byte[checked(width * height * 4 * sizeof(ushort))];
        var texelCount = width * height;
        for (var texel = 0; texel < texelCount; texel++)
        {
            var srcOffset = texel * 3 * sizeof(ushort);
            var dstOffset = texel * 4 * sizeof(ushort);
            source.Slice(srcOffset, 3 * sizeof(ushort)).CopyTo(rgba16fBytes.AsSpan(dstOffset, 3 * sizeof(ushort)));
            BinaryPrimitives.WriteUInt16LittleEndian(rgba16fBytes.AsSpan(dstOffset + 3 * sizeof(ushort), sizeof(ushort)), HalfFloatAlphaOne);
        }

        return TextureAsset.CreateRgba16Float(
            context,
            textureName,
            string.Empty,
            rgba16fBytes,
            (uint)width,
            (uint)height,
            generateMipmaps);
    }

    private TextureAsset CreateRgb16EnvironmentTextureMipChain(
        string textureName,
        string embeddedFileName,
        byte[] packed,
        int width,
        int height,
        int mipCount)
    {
        var expectedByteLength = CalculatePackedMipByteLength(width, height, 3, mipCount);
        if (packed.Length != PackedEnvironmentMipChainHeaderByteLength + expectedByteLength)
            throw new InvalidDataException($"Invalid RGB16F environment mip-chain precompile payload: {embeddedFileName}");

        var source = packed.AsSpan(PackedEnvironmentMipChainHeaderByteLength);
        var mipBytes = new byte[mipCount][];
        var sourceOffset = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var level = 0; level < mipCount; level++)
        {
            var texelCount = mipWidth * mipHeight;
            var sourceByteLength = texelCount * 3 * sizeof(ushort);
            var rgba16fBytes = new byte[texelCount * 4 * sizeof(ushort)];
            for (var texel = 0; texel < texelCount; texel++)
            {
                var srcOffset = sourceOffset + texel * 3 * sizeof(ushort);
                var dstOffset = texel * 4 * sizeof(ushort);
                source.Slice(srcOffset, 3 * sizeof(ushort)).CopyTo(rgba16fBytes.AsSpan(dstOffset, 3 * sizeof(ushort)));
                BinaryPrimitives.WriteUInt16LittleEndian(rgba16fBytes.AsSpan(dstOffset + 3 * sizeof(ushort), sizeof(ushort)), HalfFloatAlphaOne);
            }

            mipBytes[level] = rgba16fBytes;
            sourceOffset += sourceByteLength;
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        return TextureAsset.CreateRgba16FloatMipChain(
            context,
            textureName,
            string.Empty,
            mipBytes,
            (uint)width,
            (uint)height);
    }

    private TextureAsset CreateRgba16EnvironmentTexture(
        string textureName,
        string embeddedFileName,
        byte[] packed,
        int width,
        int height,
        bool generateMipmaps)
    {
        var expectedByteLength = checked(width * height * 4 * sizeof(ushort));
        if (packed.Length != PackedEnvironmentHeaderByteLength + expectedByteLength)
            throw new InvalidDataException($"Invalid RGBA16F environment precompile payload: {embeddedFileName}");

        return TextureAsset.CreateRgba16Float(
            context,
            textureName,
            string.Empty,
            packed.AsSpan(PackedEnvironmentHeaderByteLength).ToArray(),
            (uint)width,
            (uint)height,
            generateMipmaps);
    }

    private TextureAsset CreateRgba16EnvironmentTextureMipChain(
        string textureName,
        string embeddedFileName,
        byte[] packed,
        int width,
        int height,
        int mipCount)
    {
        var expectedByteLength = CalculatePackedMipByteLength(width, height, 4, mipCount);
        if (packed.Length != PackedEnvironmentMipChainHeaderByteLength + expectedByteLength)
            throw new InvalidDataException($"Invalid RGBA16F environment mip-chain precompile payload: {embeddedFileName}");

        var source = packed.AsSpan(PackedEnvironmentMipChainHeaderByteLength);
        var mipBytes = new byte[mipCount][];
        var sourceOffset = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var level = 0; level < mipCount; level++)
        {
            var byteLength = mipWidth * mipHeight * 4 * sizeof(ushort);
            mipBytes[level] = source.Slice(sourceOffset, byteLength).ToArray();
            sourceOffset += byteLength;
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        return TextureAsset.CreateRgba16FloatMipChain(
            context,
            textureName,
            string.Empty,
            mipBytes,
            (uint)width,
            (uint)height);
    }

    private enum PackedEnvironmentTextureFormat
    {
        Rgb16Float = 1,
        Rgba16Float = 2,
    }

    public bool IsDirty(SceneDirtyFlags flags) => Synchronize(() => (dirtyFlags & flags) != 0);

    internal ResourceRevisions GetResourceRevisions()
    {
        return Synchronize(() => new ResourceRevisions(meshesRevision, tlasRevision, texturesRevision, settingsRevision));
    }

    public void ClearHierarchy()
    {
        Synchronize(() =>
        {
            hierarchyRoots.Clear();
            hierarchyById.Clear();
            nextHierarchyNodeId = 0;
            TrySetSelectedHierarchyNodeIdUnsafe(-1);
            MarkHierarchyChanged();
        });
    }

    public SceneHierarchyHandle AddHierarchyRoot(SceneHierarchyNode root)
    {
        if (root is null)
            throw new ArgumentNullException(nameof(root));

        return Synchronize(() =>
        {
            hierarchyRoots.Add(root);
            if (root.Id >= 0)
                hierarchyById[root.Id] = root;
            MarkHierarchyChanged();
            return new SceneHierarchyHandle(root.Id);
        });
    }

    public SceneHierarchyHandle AddHierarchyRoot(
        string name,
        string type,
        string ownerName,
        string sourcePath,
        string? assetPath,
        SceneNodeRole role,
        SceneHierarchyNodeKind kind,
        FTransform localTransform,
        FTransform worldTransform,
        int instanceCount = 1)
    {
        return AddHierarchyNode(SceneHierarchyHandle.Invalid, name, type, ownerName, sourcePath, assetPath, role, kind, localTransform, worldTransform, instanceCount);
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
            if (child.Id >= 0)
                hierarchyById[child.Id] = child;
            MarkHierarchyChanged();
        });
    }

    public SceneHierarchyHandle AddHierarchyChild(
        SceneHierarchyHandle parent,
        string name,
        string type,
        string ownerName,
        string sourcePath,
        string? assetPath,
        SceneNodeRole role,
        SceneHierarchyNodeKind kind,
        FTransform localTransform,
        FTransform worldTransform,
        int instanceCount = 1)
    {
        return AddHierarchyNode(parent, name, type, ownerName, sourcePath, assetPath, role, kind, localTransform, worldTransform, instanceCount);
    }

    private SceneHierarchyHandle AddHierarchyNode(
        SceneHierarchyHandle parent,
        string name,
        string type,
        string ownerName,
        string sourcePath,
        string? assetPath,
        SceneNodeRole role,
        SceneHierarchyNodeKind kind,
        FTransform localTransform,
        FTransform worldTransform,
        int instanceCount)
    {
        return Synchronize(() =>
        {
            var id = nextHierarchyNodeId++;
            var parentId = parent.IsValid ? parent.Id : -1;
            var node = new SceneHierarchyNode(
                id,
                parentId,
                name,
                type,
                ownerName,
                sourcePath,
                assetPath,
                role,
                kind,
                localTransform,
                worldTransform,
                role == SceneNodeRole.Actor,
                instanceCount);
            hierarchyById[id] = node;
            if (parent.IsValid && hierarchyById.TryGetValue(parent.Id, out var parentNode))
                parentNode.Children.Add(node);
            else
                hierarchyRoots.Add(node);
            MarkHierarchyChanged();
            return new SceneHierarchyHandle(id);
        });
    }

    public void AddInstanceRange(SceneHierarchyHandle nodeHandle, int startIndex, int count)
    {
        if (!nodeHandle.IsValid || count <= 0)
            return;

        Synchronize(() =>
        {
            if (hierarchyById.TryGetValue(nodeHandle.Id, out var node))
                node.AddInstanceRange(startIndex, count);
        });
    }

    public void SetHierarchyRoots(IReadOnlyList<SceneHierarchyNode> roots)
    {
        Synchronize(() =>
        {
            hierarchyRoots.Clear();
            hierarchyById.Clear();
            hierarchyRoots.AddRange(roots);
            nextHierarchyNodeId = 0;
            foreach (var root in roots)
                IndexHierarchyNode(root);
            MarkHierarchyChanged();
        });
    }

    private void IndexHierarchyNode(SceneHierarchyNode node)
    {
        if (node.Id >= 0)
        {
            hierarchyById[node.Id] = node;
            nextHierarchyNodeId = Math.Max(nextHierarchyNodeId, node.Id + 1);
        }

        foreach (var child in node.Children)
            IndexHierarchyNode(child);
    }

    public bool TryGetHierarchyNode(SceneHierarchyHandle handle, out SceneHierarchyNode? node)
    {
        if (!handle.IsValid)
        {
            node = null;
            return false;
        }

        var result = Synchronize(() =>
        {
            hierarchyById.TryGetValue(handle.Id, out var found);
            return found;
        });
        node = result;
        return node is not null;
    }

    public bool TryGetHierarchyNodeForInstance(int instanceIndex, out SceneHierarchyNode? node)
    {
        var result = Synchronize(() =>
        {
            if (instanceIndex < 0 || instanceIndex >= meshInstances.Count)
                return null;

            var nodeId = meshInstances[instanceIndex].HierarchyNodeId;
            return nodeId >= 0 && hierarchyById.TryGetValue(nodeId, out var found) ? found : null;
        });
        node = result;
        return node is not null;
    }

    public void ClearGeometry(bool keepDefaultTestCube = false)
    {
        Synchronize(() =>
        {
            SelectedInstanceIndex = -1;
            SelectedHierarchyNodeId = -1;

            if (keepDefaultTestCube)
            {
                meshInstances.Clear();
                SetDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
                return;
            }

            context.WaitForSubmittedCommandBuffers();
            meshInstances.Clear();
            hierarchyRoots.Clear();
            hierarchyById.Clear();
            nextHierarchyNodeId = 0;
            TrySetSelectedHierarchyNodeIdUnsafe(-1);
            MarkHierarchyChanged();

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

    public bool TryGetInstanceAt(int index, out MeshInstance? instance)
    {
        lock (sync)
        {
            if (index < 0 || index >= meshInstances.Count)
            {
                instance = null;
                return false;
            }
            instance = meshInstances[index];
            return true;
        }
    }

    public IReadOnlyList<SceneHierarchyNode> GetHierarchyRootsSnapshot()
    {
        lock (sync)
            return hierarchyRoots.ToArray();
    }

    internal TextureAsset[] GetTexturesSnapshot()
    {
        lock (sync)
            return texturesByName.Values.ToArray();
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
            var selectedInstanceIndex = instanceId == ShaderDefines.INVALID_INSTANCE || instanceId >= meshInstances.Count
                ? -1
                : (int)instanceId;
            var changedSelection = TrySetSelectedInstanceIndexUnsafe(selectedInstanceIndex);
            var selectedInstance = selectedInstanceIndex >= 0 ? meshInstances[selectedInstanceIndex] : null;
            var selectedHierarchyNodeId = selectedInstance?.HierarchyNodeId ?? -1;
            var changedNode = TrySetSelectedHierarchyNodeIdUnsafe(selectedHierarchyNodeId);
            return (selectedInstance, selectedInstanceIndex >= 0, changedSelection, SelectedHierarchyNodeId, changedNode);
        });

        instance = result.Item1;
        if (result.Item3)
            SelectedInstanceChanged?.Invoke(result.Item2 ? (int)instanceId : -1);
        if (result.changedNode)
            SelectedHierarchyNodeChanged?.Invoke(result.SelectedHierarchyNodeId);
        return result.Item2;
    }

    private bool TrySetSelectedInstanceIndexUnsafe(int instanceIndex)
    {
        var normalizedIndex = instanceIndex >= 0 && instanceIndex < meshInstances.Count ? instanceIndex : -1;
        if (SelectedInstanceIndex == normalizedIndex)
            return false;

        SelectedInstanceIndex = normalizedIndex;
        return true;
    }

    private bool TrySetSelectedHierarchyNodeIdUnsafe(int nodeId)
    {
        var normalizedId = nodeId >= 0 && hierarchyById.ContainsKey(nodeId) ? nodeId : -1;
        if (SelectedHierarchyNodeId == normalizedId)
            return false;

        SelectedHierarchyNodeId = normalizedId;
        return true;
    }

    public void SetArcballPivot(Vector3 pivot)
    {
        Synchronize(() => Camera.SetArcballPivot(pivot));
    }

    public void FocusCameraOnWorldPosition(Vector3 worldPosition)
    {
        Synchronize(() => Camera.FocusOnWorldPosition(worldPosition));
    }

    public void FrameSelectedInstance()
    {
        Synchronize(() =>
        {
            var instance = SelectedInstance;
            if (instance is null) return;

            var mesh = instance.MeshAsset;
            var transform = instance.Transform;
            var min = mesh.BoundingMin;
            var max = mesh.BoundingMax;

            var c0 = new Vector3(transform.M11, transform.M21, transform.M31);
            var c1 = new Vector3(transform.M12, transform.M22, transform.M32);
            var c2 = new Vector3(transform.M13, transform.M23, transform.M33);
            var t = new Vector3(transform.M14, transform.M24, transform.M34);

            var localCenter = (min + max) * 0.5f;
            var center = localCenter.X * c0 + localCenter.Y * c1 + localCenter.Z * c2 + t;

            var half = (max - min) * 0.5f;
            var radiusSq = 0f;
            for (var i = 0; i < 8; i++)
            {
                var sx = (i & 1) == 0 ? -half.X : half.X;
                var sy = (i & 2) == 0 ? -half.Y : half.Y;
                var sz = (i & 4) == 0 ? -half.Z : half.Z;
                var dSq = (sx * c0 + sy * c1 + sz * c2).LengthSquared();
                if (dSq > radiusSq) radiusSq = dSq;
            }

            Camera.FrameBoundingBox(center, MathF.Sqrt(radiusSq));
        });
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
        IncrementResourceRevisions(flags);

        SceneDirtyFlags changedFlags;
        var immediateFlags = flags & (SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
        var deferred = flags & ~(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);

        changedFlags = SceneDirtyFlags.None;
        if (immediateFlags != SceneDirtyFlags.None)
        {
            var newlyAddedImmediateFlags = immediateFlags & ~dirtyFlags;
            dirtyFlags |= immediateFlags;
            changedFlags |= newlyAddedImmediateFlags;
        }

        if (deferred != SceneDirtyFlags.None && deferredUpdateDepth > 0)
        {
            var newlyDeferredFlags = deferred & ~deferredDirtyFlags;
            deferredDirtyFlags |= deferred;
            changedFlags |= newlyDeferredFlags;
            if (changedFlags != SceneDirtyFlags.None)
                DirtyStateChanged?.Invoke(changedFlags);
            return;
        }

        if (deferred != SceneDirtyFlags.None)
        {
            var newlyAddedDeferredFlags = deferred & ~dirtyFlags;
            dirtyFlags |= deferred;
            changedFlags |= newlyAddedDeferredFlags;
        }

        if (changedFlags != SceneDirtyFlags.None)
            DirtyStateChanged?.Invoke(changedFlags);
    }

    private void IncrementResourceRevisions(SceneDirtyFlags flags)
    {
        if ((flags & SceneDirtyFlags.Meshes) != 0)
            meshesRevision++;
        if ((flags & SceneDirtyFlags.Tlas) != 0)
            tlasRevision++;
        if ((flags & SceneDirtyFlags.Textures) != 0)
            texturesRevision++;
        if ((flags & SceneDirtyFlags.Settings) != 0)
            settingsRevision++;
    }

    private void EndDeferredUpdates()
    {
        Synchronize(() =>
        {
            if (deferredUpdateDepth <= 0)
                throw new InvalidOperationException("Deferred update scope is not active.");

            deferredUpdateDepth--;
            if (deferredUpdateDepth != 0)
                return;

            if (deferredDirtyFlags != SceneDirtyFlags.None)
            {
                dirtyFlags |= deferredDirtyFlags;
                deferredDirtyFlags = SceneDirtyFlags.None;

                dirtyFlags |= SceneDirtyFlags.Accumulation;
            }

            if (deferredHierarchyChanged)
            {
                deferredHierarchyChanged = false;
                HierarchyChanged?.Invoke();
            }
        });
    }

    private void MarkHierarchyChanged()
    {
        if (deferredUpdateDepth > 0)
        {
            deferredHierarchyChanged = true;
            return;
        }

        HierarchyChanged?.Invoke();
    }

    void IScene.SetAccumulationDirty()
    {
        SetDirty(SceneDirtyFlags.Accumulation);
    }

    void IScene.SetTlasDirty()
    {
        SetDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Accumulation);
    }

    internal byte[] BuildInstanceData()
    {
        var instances = Synchronize(() =>
        {
            if (meshInstances.Count == 0)
                return null;

            var result = new InstanceGpu[meshInstances.Count];
            for (var i = 0; i < meshInstances.Count; i++)
                result[i] = meshInstances[i].BuildInstanceData();

            return result;
        });

        if (instances is null)
        {
            var empty = new InstanceGpu
            {
                Transform = Matrix4x4.Identity,
                InverseTransform = Matrix4x4.Identity,
                NormalTransform = Matrix4x4.Identity,
                MeshId = uint.MaxValue
            };
            return StructPacking.ToBytes(new[] { empty });
        }

        return StructPacking.ToBytes(instances);
    }

    internal byte[] BuildMeshAddressData()
    {
        var addresses = Synchronize(() =>
        {
            if (meshAssetsByName.Count == 0)
                return null;

            var result = new MeshAddressesGpu[meshAssetsByName.Count];
            foreach (var meshAsset in meshAssetsByName.Values)
                result[meshAsset.MeshIndex] = meshAsset.GetBufferAddresses();

            return result;
        });

        if (addresses is null)
        {
            var empty = new MeshAddressesGpu();
            return StructPacking.ToBytes(new[] { empty });
        }

        return StructPacking.ToBytes(addresses);
    }

    internal byte[] BuildRtxInstanceData()
    {
        var instances = Synchronize(() =>
        {
            if (meshInstances.Count == 0)
                return null;

            var result = new AccelerationStructureInstanceKHR[meshInstances.Count];
            for (var i = 0; i < meshInstances.Count; i++)
            {
                result[i] = meshInstances[i].BuildRtxInstanceData();
                result[i].InstanceCustomIndex = (uint)i;
            }

            return result;
        });

        if (instances is null)
        {
            var empty = new AccelerationStructureInstanceKHR();
            return StructPacking.ToBytes(new[] { empty });
        }

        return StructPacking.ToBytes(instances);
    }

    internal byte[] BuildRtxInstanceBufferData()
    {
        var instances = Synchronize(() =>
        {
            if (meshInstances.Count == 0)
                return null;

            var result = new InstanceGpu[meshInstances.Count];
            for (var i = 0; i < meshInstances.Count; i++)
                result[i] = meshInstances[i].BuildInstanceData();

            return result;
        });

        if (instances is null)
        {
            var empty = new InstanceGpu
            {
                NormalTransform = Matrix4x4.Identity,
                MeshId = uint.MaxValue
            };
            return StructPacking.ToBytes(new[] { empty });
        }

        return StructPacking.ToBytes(instances);
    }

    public void Dispose()
    {
        context.WaitForSubmittedCommandBuffers();
        Camera.Changed -= OnCameraUpdated;
        Camera.ProjectionChangeRequested -= OnCameraProjectionChangeRequested;
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

    internal void SetCameraProjection(CameraProjectionType type)
    {
        Synchronize(() =>
        {
            if (Camera.Projection == type)
                return;

            Camera.Changed -= OnCameraUpdated;
            Camera.ProjectionChangeRequested -= OnCameraProjectionChangeRequested;
            Camera.DetachSettings();

            var settings = Camera.Settings;
            CameraBase newCamera = type switch
            {
                CameraProjectionType.Orthographic => new OrthographicCamera(input, settings),
                CameraProjectionType.Fisheye => new FisheyeCamera(input, settings),
                _ => new PerspectiveCamera(input, settings),
            };

            newCamera.SetPosition(Camera.Position);
            newCamera.SetRotation(Camera.Rotation);
            newCamera.SetArcballPivot(Camera.ArcballPivot);

            Camera = newCamera;
            Camera.Changed += OnCameraUpdated;
            Camera.ProjectionChangeRequested += OnCameraProjectionChangeRequested;
            OnCameraUpdated();
        });
    }

    private void OnCameraProjectionChangeRequested(CameraProjectionType type) => SetCameraProjection(type);

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

        if (e.PropertyName == nameof(RenderSettings.BufferVisualization))
            return;

        SetDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
    }
}
