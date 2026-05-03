using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Threading;
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
    private const int PackedEnvironmentMagic = 0x504D4555; // UEMP
    private const int PackedEnvironmentMipChainMagic = 0x324D4555; // UEM2
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
    private readonly List<SceneHierarchyNode> hierarchyRoots = [];
    private readonly object sync = new();
    private readonly List<MeshInstance> meshInstances = [];
    private readonly Dictionary<string, TextureAsset> texturesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeshAsset> meshAssetsByName = new(StringComparer.OrdinalIgnoreCase);
    private SceneDirtyFlags dirtyFlags;
    private SceneDirtyFlags deferredDirtyFlags;
    private int deferredUpdateDepth;
    private ulong meshesRevision;
    private ulong tlasRevision;
    private ulong texturesRevision;
    private ulong settingsRevision;
    private Func<Func<object?>, object?>? gpuDispatcher;

    public int SelectedInstanceIndex { get; private set; } = -1;
    public MeshInstance? SelectedInstance =>
        SelectedInstanceIndex >= 0 && SelectedInstanceIndex < meshInstances.Count
            ? meshInstances[SelectedInstanceIndex]
            : null;
    public event Action<int>? SelectedInstanceChanged;
    public Camera Camera { get; }
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

    /// <summary>
    /// Gets or adds a named asset using double-checked locking.
    /// The factory is called OUTSIDE the lock to avoid blocking readers during expensive GPU allocation.
    /// </summary>
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

        // First check without lock - fast path for existing assets
        TAsset? result;
        lock (sync)
        {
            if (itemsByName.TryGetValue(name, out var existing))
            {
                added = false;
                return existing;
            }
        }

        // Factory call OUTSIDE lock - expensive GPU allocation happens here
        var created = factory();
        if (created is null)
        {
            added = false;
            return null;
        }

        // Second check with lock - another thread might have added it
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

    internal RasterCameraDataGpu CaptureRasterCameraData()
    {
        return Synchronize(() => new RasterCameraDataGpu
        {
            Position = Camera.Position,
            Direction = Camera.ToStruct().Direction,
            Horizontal = Camera.ToStruct().Horizontal,
            Vertical = Camera.ToStruct().Vertical,
            FocalLength = Camera.ToStruct().FocalLength,
            NearPlane = 0.01f,
            FarPlane = 10_000f,
            WorldToClip = Camera.WorldToClip
        });
    }

    internal RasterShadowDataGpu CaptureRasterShadowData()
    {
        return Synchronize(() =>
        {
            const float atlasSize = 4096f;
            const float pageSize = 256f;
            const int cascadeCount = 4;

            var environment = Environment.ToStruct();
            var enabled = RenderSettings.RasterVirtualShadowMapsEnabled &&
                          environment.DirectionalIntensity > 0.0001f &&
                          environment.DirectionalDirection.LengthSquared() > 0.0001f;
            var result = new RasterShadowDataGpu
            {
                UvScaleOffset0 = new Vector4(0.5f, 0.5f, 0.0f, 0.0f),
                UvScaleOffset1 = new Vector4(0.5f, 0.5f, 0.5f, 0.0f),
                UvScaleOffset2 = new Vector4(0.5f, 0.5f, 0.0f, 0.5f),
                UvScaleOffset3 = new Vector4(0.5f, 0.5f, 0.5f, 0.5f),
                CascadeFarDistances = new Vector4(18f, 55f, 160f, 480f),
                AtlasSizePageSizeCascadeCountEnabled = new Vector4(atlasSize, pageSize, cascadeCount, enabled ? 1f : 0f)
            };

            if (!enabled)
                return result;

            var cameraData = Camera.ToStruct();
            var lightDirection = Vector3.Normalize(environment.DirectionalDirection);
            var cameraForward = Vector3.Normalize(cameraData.Direction);
            var cameraHorizontalTan = MathF.Max(0.001f, cameraData.Horizontal.Length());
            var cameraVerticalTan = MathF.Max(0.001f, cameraData.Vertical.Length());
            var shadowMatrices = new[]
            {
                BuildDirectionalShadowWorldToClip(cameraData.Position, cameraForward, lightDirection, 18f, cameraHorizontalTan, cameraVerticalTan),
                BuildDirectionalShadowWorldToClip(cameraData.Position, cameraForward, lightDirection, 55f, cameraHorizontalTan, cameraVerticalTan),
                BuildDirectionalShadowWorldToClip(cameraData.Position, cameraForward, lightDirection, 160f, cameraHorizontalTan, cameraVerticalTan),
                BuildDirectionalShadowWorldToClip(cameraData.Position, cameraForward, lightDirection, 480f, cameraHorizontalTan, cameraVerticalTan)
            };

            result.WorldToClip0 = shadowMatrices[0];
            result.WorldToClip1 = shadowMatrices[1];
            result.WorldToClip2 = shadowMatrices[2];
            result.WorldToClip3 = shadowMatrices[3];
            return result;
        });
    }

    private static Matrix4x4 BuildDirectionalShadowWorldToClip(
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 lightDirection,
        float cascadeFar,
        float cameraHorizontalTan,
        float cameraVerticalTan)
    {
        var halfDepth = cascadeFar * 0.5f;
        var center = cameraPosition + cameraForward * halfDepth;
        var halfExtent = MathF.Max(cascadeFar * cameraHorizontalTan, cascadeFar * cameraVerticalTan);
        halfExtent = MathF.Max(halfExtent, 4f);

        var lightForward = Vector3.Normalize(-lightDirection);
        var upCandidate = MathF.Abs(Vector3.Dot(lightForward, Vector3.UnitY)) > 0.95f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(upCandidate, lightForward));
        var up = Vector3.Normalize(Vector3.Cross(lightForward, right));
        var lightDepth = MathF.Max(cascadeFar + halfExtent * 2f, 16f);
        var lightPosition = center - lightForward * (lightDepth * 0.5f);

        var view = new Matrix4x4(
            right.X, up.X, lightForward.X, 0f,
            right.Y, up.Y, lightForward.Y, 0f,
            right.Z, up.Z, lightForward.Z, 0f,
            -Vector3.Dot(lightPosition, right),
            -Vector3.Dot(lightPosition, up),
            -Vector3.Dot(lightPosition, lightForward),
            1f);

        var width = halfExtent * 2f;
        var height = halfExtent * 2f;
        var nearPlane = 0f;
        var farPlane = lightDepth;
        var projection = new Matrix4x4(
            2f / width, 0f, 0f, 0f,
            0f, 2f / height, 0f, 0f,
            0f, 0f, 1f / MathF.Max(0.0001f, farPlane - nearPlane), 0f,
            0f, 0f, -nearPlane / MathF.Max(0.0001f, farPlane - nearPlane), 1f);

        return Matrix4x4.Transpose(view * projection);
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
        var selectedInstanceIndex = Synchronize(() =>
        {
            if (!TrySetSelectedInstanceIndexUnsafe(instanceIndex))
                return int.MinValue;

            return SelectedInstanceIndex;
        });

        if (selectedInstanceIndex != int.MinValue)
            SelectedInstanceChanged?.Invoke(selectedInstanceIndex);
    }

    public void ClearSelection()
    {
        if (Synchronize(() => TrySetSelectedInstanceIndexUnsafe(-1)))
            SelectedInstanceChanged?.Invoke(-1);
    }

    public void SetEnvironment(TextureAsset texture, TextureAsset? cdfTexture = null, TextureAsset? irradianceMap = null, TextureAsset? radianceMap = null)
    {
        Synchronize(() =>
        {
            if (texture.Index < 0)
                Add(texture);
            if (cdfTexture is not null && cdfTexture.Index < 0)
                Add(cdfTexture);
            if (irradianceMap is not null && irradianceMap.Index < 0)
                Add(irradianceMap);
            if (radianceMap is not null && radianceMap.Index < 0)
                Add(radianceMap);

            Environment.TextureIndex = texture.Index;
            Environment.CdfTextureIndex = cdfTexture?.Index ?? -1;
            Environment.IrradianceMapIndex = irradianceMap?.Index ?? -1;
            Environment.RadianceMapIndex = radianceMap?.Index ?? -1;
            var lodSource = radianceMap?.Image ?? texture.Image;
            Environment.MaxTextureLod = Math.Max(lodSource.MipLevels - 1u, 0u);
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

    public void TryLoadDefaultEnvironment()
    {
        const string hdriName = "default_hdri";

        try
        {
            var environment = CreateEnvironmentMapTexture(hdriName, $"Assets/Precomputed/{hdriName}_env.bin", generateMipmaps: true);
            var cdf = CreateEnvironmentMapTexture($"{hdriName}_cdf", $"Assets/Precomputed/{hdriName}_cdf.bin");
            var irradiance = CreateEnvironmentMapTexture($"{hdriName}_irradiance", $"Assets/Precomputed/{hdriName}_irradiance.bin");
            var radiance = CreateEnvironmentMapTexture($"{hdriName}_radiance", $"Assets/Precomputed/{hdriName}_radiance.bin", generateMipmaps: true);

            Add(environment);
            Add(cdf);
            Add(irradiance);
            Add(radiance);
            SetEnvironment(environment, cdf, irradiance, radiance);
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
        hierarchyRoots.Clear();
    }

    public void AddHierarchyRoot(SceneHierarchyNode root)
    {
        if (root is null)
            throw new ArgumentNullException(nameof(root));

        hierarchyRoots.Add(root);
    }

    public void AddHierarchyChild(SceneHierarchyNode parent, SceneHierarchyNode child)
    {
        if (parent is null)
            throw new ArgumentNullException(nameof(parent));
        if (child is null)
            throw new ArgumentNullException(nameof(child));

        parent.Children.Add(child);
    }

    public void SetHierarchyRoots(IReadOnlyList<SceneHierarchyNode> roots)
    {
        hierarchyRoots.Clear();
        hierarchyRoots.AddRange(roots);
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
            return (selectedInstance, selectedInstanceIndex >= 0, changedSelection);
        });

        instance = result.Item1;
        if (result.Item3)
            SelectedInstanceChanged?.Invoke(result.Item2 ? (int)instanceId : -1);
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
        IncrementResourceRevisions(flags);

        SceneDirtyFlags changedFlags;
        // Camera and settings updates must always be applied immediately to ensure
        // the camera can update every frame regardless of batch updates.
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
            if (deferredUpdateDepth != 0 || deferredDirtyFlags == SceneDirtyFlags.None)
                return;

            dirtyFlags |= deferredDirtyFlags;
            deferredDirtyFlags = SceneDirtyFlags.None;
            
            // Always mark accumulation as dirty after batch updates to ensure
            // the first frame properly resets all accumulated state
            dirtyFlags |= SceneDirtyFlags.Accumulation;
        });
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
            var index = 0;
            foreach (var meshAsset in meshAssetsByName.Values)
                result[index++] = meshAsset.GetBufferAddresses();

            return result;
        });

        if (addresses is null)
        {
            var empty = new MeshAddressesGpu();
            return StructPacking.ToBytes(new[] { empty });
        }

        return StructPacking.ToBytes(addresses);
    }

    internal byte[] BuildMeshRasterMetadata()
    {
        var metadata = Synchronize(() =>
        {
            if (meshAssetsByName.Count == 0)
                return null;

            var instanceCapacities = new uint[meshAssetsByName.Count];
            for (var i = 0; i < meshInstances.Count; i++)
            {
                var meshIndex = meshInstances[i].GetMeshIndex();
                if (meshIndex < instanceCapacities.Length)
                    instanceCapacities[meshIndex]++;
            }

            var result = new MeshRasterMetadataGpu[meshAssetsByName.Count];
            uint runningOffset = 0;
            foreach (var meshAsset in meshAssetsByName.Values)
            {
                var meshIndex = meshAsset.MeshIndex;
                var capacity = meshIndex < instanceCapacities.Length ? instanceCapacities[meshIndex] : 0u;
                result[meshIndex] = new MeshRasterMetadataGpu
                {
                    VisibleInstanceOffset = runningOffset,
                    InstanceCapacity = capacity,
                    IndexCount = meshAsset.GetBufferAddresses().IndexCount
                };
                runningOffset += capacity;
            }

            return result;
        });

        if (metadata is null)
            return StructPacking.ToBytes(new[] { new MeshRasterMetadataGpu() });

        return StructPacking.ToBytes(metadata);
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
                // Set InstanceCustomIndex to the instance array index for unique identification.
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

        if (e.PropertyName == nameof(RenderSettings.BufferVisualization))
            return;

        SetDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
    }
}
