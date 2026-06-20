using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Platform;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Raytracing;

internal abstract unsafe class GpuRaytracer : IGpuRenderPath
{
    protected const uint MaxTextures = 10_000;
    protected const uint WavefrontCounterCount = 4;
    protected const uint WavefrontQueueGroupSize = 128;
    protected const uint MaxWavefrontWorkers = WavefrontQueueGroupSize * 64;
    protected const ulong PathStateGpuSize = 128;
    protected const ulong PathRayGpuSize = 48;
    protected const ulong HitWorkItemGpuSize = 48;
    protected const ulong ShadowWorkItemGpuSize = 64;
    public int ShaderPixelSizePercent { get; set; } = 100;

    protected readonly struct PipelineBundle
    {
        public PipelineBundle(
            Pipeline generate,
            Pipeline extend,
            Pipeline shade,
            Pipeline connect,
            Pipeline advance,
            Pipeline finalize)
        {
            Generate = generate;
            Extend = extend;
            Shade = shade;
            Connect = connect;
            Advance = advance;
            Finalize = finalize;
        }

        public Pipeline Generate { get; }
        public Pipeline Extend { get; }
        public Pipeline Shade { get; }
        public Pipeline Connect { get; }
        public Pipeline Advance { get; }
        public Pipeline Finalize { get; }
    }

    protected readonly Context Context;
    protected readonly Scene Scene;
    protected readonly VulkanDeviceResources DeviceResources;
    protected RenderMode CachedRenderMode;
    private bool hasLoggedCurrentRenderMode;
    private bool settingsDirty;

    private VulkanImage? outputColorImage;
    private VulkanImage? albedoImage;
    private VulkanImage? normalImage;
    private VulkanImage? cryptoImage;
    private VulkanImage? positionImage;
    private VulkanImage? adaptiveStateImage;
    private VulkanBuffer? sceneSettingsBuffer;
    private VulkanBuffer? sceneSettingsUploadBuffer;
    private PixelSize renderImageSize;

    private ulong lastBoundColorImageViewHandle;
    private ulong uploadedTexturesRevision = ulong.MaxValue;
    protected uint FrameIndex;
    private bool hasLoggedFirstRender;
    protected VulkanBuffer WavefrontCountersBuffer = null!;
    protected VulkanBuffer PathStateBuffer = null!;
    protected VulkanBuffer RayQueueABuffer = null!;
    protected VulkanBuffer RayQueueBBuffer = null!;
    protected VulkanBuffer HitQueueBuffer = null!;
    protected VulkanBuffer ShadowQueueBuffer = null!;
    protected PixelSize WavefrontBufferSize;
    protected int WavefrontBufferSamples;

    private SceneSettingsDataGpu lastUploadedSettings;

    protected GpuRaytracer(Context context, Scene scene)
    {
        Context = context;
        Scene = scene;
        DeviceResources = new VulkanDeviceResources(context);
        CachedRenderMode = scene.RenderSettings.RenderMode;
        settingsDirty = true;

        scene.RenderSettings.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(RenderSettings.RenderMode))
            {
                CachedRenderMode = scene.RenderSettings.RenderMode;
                hasLoggedCurrentRenderMode = false;
                Log.Information("Render mode changed to {RenderMode}.", CachedRenderMode);
            }
            settingsDirty = true;
        };
        scene.Camera.Changed += () => settingsDirty = true;
        scene.Environment.PropertyChanged += (s, e) => settingsDirty = true;
    }

    public static bool PreferHardwareBackend(Context context, RenderMode renderMode)
    {
#if DISABLE_RTX
        context = context;
        renderMode = renderMode;
        return false;
#else
        return context.RayTracingSupported && renderMode is RenderMode.AmbientOcclusion or RenderMode.PathTracing;
#endif
    }

    public static GpuRaytracer Create(Context context, Scene scene)
    {
#if DISABLE_RTX
        Log.Information("Using Compute raytracer backend for render mode {RenderMode}.", scene.RenderSettings.RenderMode);
        return new ComputeRaytracer(context, scene);
#else
        if (!PreferHardwareBackend(context, scene.RenderSettings.RenderMode))
        {
            Log.Information("Using Compute raytracer backend for render mode {RenderMode}.", scene.RenderSettings.RenderMode);
            return new ComputeRaytracer(context, scene);
        }

        try
        {
            var rtxRaytracer = new RtxRaytracer(context, scene);
            Log.Information("Using RTX ray-query raytracer backend.");
            return rtxRaytracer;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RTX ray-query raytracer creation failed, falling back to compute.");
            var computeRaytracer = new ComputeRaytracer(context, scene);
            Log.Information("Using Compute raytracer backend.");
            return computeRaytracer;
        }
#endif
    }

    public VulkanImage OutputColor => outputColorImage ?? throw new InvalidOperationException("Raytracer output image is not initialized");
    public VulkanImage OutputAlbedo => albedoImage ?? throw new InvalidOperationException("Raytracer albedo image is not initialized");
    public VulkanImage OutputNormal => normalImage ?? throw new InvalidOperationException("Raytracer normal image is not initialized");
    public VulkanImage OutputCrypto => cryptoImage ?? throw new InvalidOperationException("Raytracer crypto image is not initialized");
    public VulkanImage OutputPosition => positionImage ?? throw new InvalidOperationException("Raytracer position image is not initialized");
    public VulkanImage OutputAdaptiveState => adaptiveStateImage ?? throw new InvalidOperationException("Raytracer adaptive-state image is not initialized");
    public PixelSize RenderImageSize => renderImageSize;
    public void Record(PixelSize renderSize, VulkanImage image, Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData)
    {
        EnsureRenderImages(renderSize, commandBuffer);
        UpdateSceneResources(commandBuffer, force: false);
        if (settingsDirty || Scene.IsDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings))
            FrameIndex = 0;

        if (lastBoundColorImageViewHandle != outputColorImage?.ViewHandle)
            UpdateOutputImageBindings();

        if (settingsDirty)
            UpdateSceneSettingsBuffer(commandBuffer, renderData, force: false);

        UpdateTextureBindings();

        if (!hasLoggedFirstRender || FrameIndex < 3)
        {
            Log.Debug(
                "{RaytracerType} frame={Frame} size={Width}x{Height} envTex={EnvironmentTextureIndex} moving={IsMoving} pixelSizePercent={PixelSizePercent} mode={RenderMode}",
                GetType().Name,
                FrameIndex,
                renderSize.Width,
                renderSize.Height,
                renderData.EnvironmentTextureIndex,
                renderData.IsMoving,
                ShaderPixelSizePercent,
                CachedRenderMode);
            hasLoggedFirstRender = true;
        }

        if (!hasLoggedCurrentRenderMode)
        {
            Log.Information("Raytracer using {RenderMode} mode.", EffectiveRenderMode);
            hasLoggedCurrentRenderMode = true;
        }

        outputColorImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        albedoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        normalImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        cryptoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        positionImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        adaptiveStateImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        ExecuteRaytracing(commandBuffer, outputColorImage, CreatePushConstants(FrameIndex, renderData));
        FrameIndex++;
        Scene.ClearDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
    }

    protected abstract void ExecuteRaytracing(Context.CommandBuffer commandBuffer, VulkanImage image, PushDataGpu pushConstants);
    protected abstract void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force);
    protected abstract DescriptorSet GetDescriptorSet();
    protected abstract DescriptorSetLayout GetDescriptorSetLayout();
    protected virtual uint OutputImageBindingBase => 1;
    protected virtual uint SceneSettingsBinding => 8;
    protected virtual uint TextureArrayBinding => 9;
    protected virtual uint RtxInstanceBufferBinding => 1;

    protected RenderMode EffectiveRenderMode
    {
        get => CachedRenderMode;
    }

    protected int GetWavefrontSamplesPerFrame(PushDataGpu pushConstants)
    {
        if (EffectiveRenderMode == RenderMode.PathTracing)
            return 1;

        if (pushConstants.IsMoving != 0 && pushConstants.PixelSizePercent > 100)
            return 1;

        return Math.Max(1, Scene.RenderSettings.SamplesPerPixel);
    }

    protected void UpdateOutputImageBindings()
    {
        if (outputColorImage is null)
            throw new InvalidOperationException("Raytracer output images are not initialized.");

        if (lastBoundColorImageViewHandle == outputColorImage.ViewHandle)
            return;

        var descriptorSet = GetDescriptorSet();

        new VulkanDescriptorWriter()
            .StorageImage(OutputImageBindingBase + 0, outputColorImage)
            .StorageImage(OutputImageBindingBase + 1, albedoImage!)
            .StorageImage(OutputImageBindingBase + 2, normalImage!)
            .StorageImage(OutputImageBindingBase + 3, cryptoImage!)
            .StorageImage(OutputImageBindingBase + 4, positionImage!)
            .StorageImage(OutputImageBindingBase + 5, adaptiveStateImage!)
            .Update(Context, descriptorSet);
        lastBoundColorImageViewHandle = outputColorImage.ViewHandle;
        Log.Debug("Updated output image bindings (color/albedo/normal/crypto/position/adaptive-state).");
    }

    private bool UpdateSceneSettingsBinding()
    {
        if (sceneSettingsBuffer is not null)
            return false;

        sceneSettingsBuffer = new VulkanBuffer(
            Context,
            (ulong)Marshal.SizeOf<SceneSettingsDataGpu>(),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);

        new VulkanDescriptorWriter()
            .StorageBuffer(SceneSettingsBinding, sceneSettingsBuffer)
            .Update(Context, GetDescriptorSet());
        return true;
    }

    private void UpdateSceneSettingsBuffer(Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData, bool force)
    {
        var created = UpdateSceneSettingsBinding();
        if (sceneSettingsBuffer is null)
            throw new InvalidOperationException("Scene settings buffer is not initialized.");

        if (!force && !created && !settingsDirty)
            return;

        sceneSettingsUploadBuffer ??= new VulkanBuffer(
            Context,
            (ulong)Unsafe.SizeOf<SceneSettingsDataGpu>(),
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        var settings = new SceneSettingsDataGpu
        {
            RenderSettings = renderData.RenderSettings,
            Environment = renderData.Environment,
        };

        Span<SceneSettingsDataGpu> settingsSpan = stackalloc SceneSettingsDataGpu[1];
        settingsSpan[0] = settings;
        sceneSettingsUploadBuffer.Upload(MemoryMarshal.AsBytes(settingsSpan));
        CopyBuffer(commandBuffer.InternalHandle, sceneSettingsUploadBuffer, sceneSettingsBuffer, (ulong)Unsafe.SizeOf<SceneSettingsDataGpu>());
        lastUploadedSettings = settings;
        settingsDirty = false;
    }

    private void UpdateTextureBindings()
    {
        var revisions = Scene.GetResourceRevisions();
        if (uploadedTexturesRevision == revisions.Textures)
            return;

        var textures = Scene.GetTexturesSnapshot();
        if (textures.Length > MaxTextures)
            throw new InvalidOperationException($"Too many textures for bindless descriptor array ({textures.Length} > {MaxTextures}).");

        if (textures.Length == 0)
        {
            Log.Information("No textures bound for bindless descriptor array.");
            uploadedTexturesRevision = revisions.Textures;
            return;
        }

        var descriptors = new DescriptorImageInfo[textures.Length];
        for (var i = 0; i < textures.Length; i++)
            descriptors[i] = textures[i].GetDescriptorImageInfo();

        new VulkanDescriptorWriter()
            .CombinedImageSamplers(TextureArrayBinding, descriptors)
            .Update(Context, GetDescriptorSet());

        uploadedTexturesRevision = revisions.Textures;
        Log.Debug("Updated bindless texture descriptors: count={TextureCount}", descriptors.Length);
    }

    private void EnsureRenderImages(PixelSize size, Context.CommandBuffer commandBuffer)
    {
        if (outputColorImage is not null && renderImageSize == size)
            return;

        if (outputColorImage is not null)
        {
            Context.WaitForSubmittedCommandBuffers();
            DisposeRenderImages();
        }

        var supportedHandles = GetSupportedHandleTypes();
        outputColorImage = new VulkanImage(Context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        albedoImage = new VulkanImage(Context, (uint)Format.R8G8B8A8Unorm, size, false, supportedHandles);
        normalImage = new VulkanImage(Context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        cryptoImage = new VulkanImage(Context, (uint)Format.R32Uint, size, false, supportedHandles);
        positionImage = new VulkanImage(Context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        adaptiveStateImage = new VulkanImage(Context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        renderImageSize = size;
        lastBoundColorImageViewHandle = 0;
        FrameIndex = 0;
        ClearRenderImages(commandBuffer);
        Log.Information("Recreated raytracer intermediate images: {Width}x{Height}", size.Width, size.Height);
    }

    private unsafe void ClearRenderImages(Context.CommandBuffer commandBuffer)
    {
        ClearColorImage(outputColorImage!, ImageLayout.TransferDstOptimal, new ClearColorValue(0f, 0f, 0f, 0f), commandBuffer);
        ClearColorImage(albedoImage!, ImageLayout.TransferDstOptimal, new ClearColorValue(0f, 0f, 0f, 0f), commandBuffer);
        ClearColorImage(normalImage!, ImageLayout.TransferDstOptimal, new ClearColorValue(0f, 0f, 0f, 0f), commandBuffer);
        ClearColorImage(cryptoImage!, ImageLayout.TransferDstOptimal, new ClearColorValue(ShaderDefines.INVALID_INSTANCE, 0u, 0u, 0u), commandBuffer);
        ClearColorImage(positionImage!, ImageLayout.TransferDstOptimal, new ClearColorValue(0f, 0f, 0f, 0f), commandBuffer);
        ClearColorImage(adaptiveStateImage!, ImageLayout.TransferDstOptimal, new ClearColorValue(0f, 0f, 0f, 0f), commandBuffer);
    }

    private unsafe void ClearColorImage(VulkanImage image, ImageLayout clearLayout, ClearColorValue clearValue, Context.CommandBuffer commandBuffer)
    {
        image.TransitionLayout(commandBuffer.InternalHandle, clearLayout, AccessFlags.TransferWriteBit);

        var subresourceRange = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        Context.Api.CmdClearColorImage(
            commandBuffer.InternalHandle,
            image.InternalHandle,
            clearLayout,
            in clearValue,
            1,
            in subresourceRange);
    }

    protected void DisposeRenderImages()
    {
        outputColorImage?.Dispose();
        outputColorImage = null;
        albedoImage?.Dispose();
        albedoImage = null;
        normalImage?.Dispose();
        normalImage = null;
        cryptoImage?.Dispose();
        cryptoImage = null;
        positionImage?.Dispose();
        positionImage = null;
        adaptiveStateImage?.Dispose();
        adaptiveStateImage = null;
        renderImageSize = default;
        lastBoundColorImageViewHandle = 0;
    }

    public bool QueryPixelUInt(VulkanImage image, int pixelX, int pixelY, out uint value)
    {
        value = 0;
        if (image is null)
            return false;

        if (image.CurrentLayout == (uint)ImageLayout.Undefined)
            return false;

        var maxX = renderImageSize.Width - 1;
        var maxY = renderImageSize.Height - 1;
        if (pixelX < 0 || pixelY < 0 || pixelX > maxX || pixelY > maxY)
            return false;

        value = ReadPixelUInt(image, pixelX, pixelY);
        return true;
    }

    public bool QueryPixelHalf4(VulkanImage image, int pixelX, int pixelY, out Vector4 value)
    {
        value = default;
        if (image is null)
            return false;

        if (image.CurrentLayout == (uint)ImageLayout.Undefined)
            return false;

        var maxX = renderImageSize.Width - 1;
        var maxY = renderImageSize.Height - 1;
        if (pixelX < 0 || pixelY < 0 || pixelX > maxX || pixelY > maxY)
            return false;

        value = ReadPixelHalf4(image, pixelX, pixelY);
        return true;
    }

    protected PushDataGpu CreatePushConstants(uint frame, Scene.RenderDataGpu renderData)
    {
        return new PushDataGpu
        {
            Frame = (int)frame,
            IsMoving = renderData.IsMoving,
            PixelSizePercent = ShaderPixelSizePercent,
            Camera = renderData.Camera
        };
    }

    protected void CreatePrimaryDescriptorSet(out DescriptorSetLayout layout, out DescriptorSet set)
    {
        layout = DeviceResources.Track(new VulkanDescriptorSetBuilder()
            .Add(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(6, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(7, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(8, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(
                9,
                DescriptorType.CombinedImageSampler,
                MaxTextures,
                ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit,
                DescriptorBindingFlags.PartiallyBoundBit |
                DescriptorBindingFlags.VariableDescriptorCountBit |
                DescriptorBindingFlags.UpdateAfterBindBit)
            .BuildLayout(Context, DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit));
        set = VulkanDescriptorSet.Allocate(Context, layout, MaxTextures);
    }

    protected void CreateWavefrontDescriptorSets(out DescriptorSetLayout layout, out DescriptorSet bootstrapSet, out DescriptorSet forwardSet, out DescriptorSet reverseSet)
    {
        var builder = new VulkanDescriptorSetBuilder();
        for (var i = 0; i < 6; i++)
            builder.Add((uint)i, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);

        layout = DeviceResources.Track(builder.BuildLayout(Context));
        bootstrapSet = VulkanDescriptorSet.Allocate(Context, layout);
        forwardSet = VulkanDescriptorSet.Allocate(Context, layout);
        reverseSet = VulkanDescriptorSet.Allocate(Context, layout);
    }

    protected VulkanBuffer CreateHostVisibleStorageBuffer(ulong size)
    {
        return new VulkanBuffer(
            Context,
            Math.Max(size, 16),
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            new byte[Math.Max((int)size, 16)]);
    }

    protected VulkanBuffer CreateWavefrontStorageBuffer(ulong size)
    {
        return new VulkanBuffer(
            Context,
            Math.Max(size, 16),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);
    }

    protected VulkanBuffer CreateDeviceLocalBuffer(ulong size, BufferUsageFlags usage)
    {
        return new VulkanBuffer(
            Context,
            Math.Max(size, 16),
            usage | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);
    }

    protected void InitializeWavefrontBuffers()
    {
        WavefrontCountersBuffer = CreateWavefrontStorageBuffer(16);
        PathStateBuffer = CreateWavefrontStorageBuffer(16);
        RayQueueABuffer = CreateWavefrontStorageBuffer(16);
        RayQueueBBuffer = CreateWavefrontStorageBuffer(16);
        HitQueueBuffer = CreateWavefrontStorageBuffer(16);
        ShadowQueueBuffer = CreateWavefrontStorageBuffer(16);
    }

    protected void UpdateMeshSceneBuffers(Context.CommandBuffer commandBuffer, ref VulkanBuffer instancesBuffer, ref VulkanBuffer meshBuffer, DescriptorSet descriptorSet)
    {
        var instanceBytes = Scene.BuildInstanceData();
        var meshBytes = Scene.BuildMeshAddressData();

        var previousInstancesBuffer = instancesBuffer;
        var previousMeshBuffer = meshBuffer;

        instancesBuffer = UploadDeviceLocalBuffer(
            commandBuffer,
            instanceBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshBuffer = UploadDeviceLocalBuffer(
            commandBuffer,
            meshBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);

        if (previousInstancesBuffer.Handle.Handle != default)
            Context.RetainForExecution(commandBuffer, previousInstancesBuffer);
        if (previousMeshBuffer.Handle.Handle != default)
            Context.RetainForExecution(commandBuffer, previousMeshBuffer);

        new VulkanDescriptorWriter()
            .StorageBuffer(0, instancesBuffer)
            .StorageBuffer(7, meshBuffer)
            .Update(Context, descriptorSet);
    }

    protected void EnsureWavefrontBuffers(
        Context.CommandBuffer commandBuffer,
        PixelSize imageSize,
        PushDataGpu pushConstants,
        DescriptorSet wavefrontBootstrapDescriptorSet,
        DescriptorSet wavefrontForwardDescriptorSet,
        DescriptorSet wavefrontReverseDescriptorSet,
        string backendName)
    {
        var requiredSamples = GetWavefrontSamplesPerFrame(pushConstants);
        if (WavefrontBufferSize == imageSize && WavefrontBufferSamples == requiredSamples)
            return;

        Context.WaitForSubmittedCommandBuffers();
        DisposeWavefrontResources();

        var queueCapacity = Math.Max(1u, ComputeWavefrontQueueCapacity(imageSize, pushConstants));
        var pathStateSize = queueCapacity * PathStateGpuSize;
        var rayQueueSize = queueCapacity * PathRayGpuSize;
        var hitQueueSize = queueCapacity * HitWorkItemGpuSize;
        var shadowQueueSize = (queueCapacity * 2ul) * ShadowWorkItemGpuSize;
        WavefrontCountersBuffer = CreateWavefrontStorageBuffer(WavefrontCounterCount * sizeof(uint));
        PathStateBuffer = CreateWavefrontStorageBuffer(pathStateSize);
        RayQueueABuffer = CreateWavefrontStorageBuffer(rayQueueSize);
        RayQueueBBuffer = CreateWavefrontStorageBuffer(rayQueueSize);
        HitQueueBuffer = CreateWavefrontStorageBuffer(hitQueueSize);
        ShadowQueueBuffer = CreateWavefrontStorageBuffer(shadowQueueSize);

        UpdateWavefrontFixedBindings(wavefrontBootstrapDescriptorSet);
        UpdateWavefrontFixedBindings(wavefrontForwardDescriptorSet);
        UpdateWavefrontFixedBindings(wavefrontReverseDescriptorSet);
        UpdateWavefrontQueueBindings(wavefrontBootstrapDescriptorSet, RayQueueABuffer, RayQueueABuffer);
        UpdateWavefrontQueueBindings(wavefrontForwardDescriptorSet, RayQueueABuffer, RayQueueBBuffer);
        UpdateWavefrontQueueBindings(wavefrontReverseDescriptorSet, RayQueueBBuffer, RayQueueABuffer);

        WavefrontBufferSize = imageSize;
        WavefrontBufferSamples = requiredSamples;
        Log.Information(
            "{Backend} wavefront buffers allocated: size={Width}x{Height} samples={Samples} queueCapacity={QueueCapacity}",
            backendName,
            imageSize.Width,
            imageSize.Height,
            requiredSamples,
            queueCapacity);
        Log.Debug(
            "{Backend} wavefront buffer sizes: pathStateBytes={PathStateBytes} rayQueueBytes={RayQueueBytes} hitQueueBytes={HitQueueBytes} shadowQueueBytes={ShadowQueueBytes}",
            backendName,
            pathStateSize,
            rayQueueSize,
            hitQueueSize,
            shadowQueueSize);
    }

    protected void UpdateWavefrontFixedBindings(DescriptorSet wavefrontDescriptorSet)
    {
        new VulkanDescriptorWriter()
            .StorageBuffer(0, WavefrontCountersBuffer)
            .StorageBuffer(1, PathStateBuffer)
            .StorageBuffer(4, HitQueueBuffer)
            .StorageBuffer(5, ShadowQueueBuffer)
            .Update(Context, wavefrontDescriptorSet);
    }

    protected void UpdateWavefrontQueueBindings(DescriptorSet wavefrontDescriptorSet, VulkanBuffer inputQueue, VulkanBuffer outputQueue)
    {
        new VulkanDescriptorWriter()
            .StorageBuffer(2, inputQueue)
            .StorageBuffer(3, outputQueue)
            .Update(Context, wavefrontDescriptorSet);
    }

    protected static PixelSize ComputeWavefrontShadingSize(PixelSize imageSize, PushDataGpu pushConstants)
    {
        var width = imageSize.Width;
        var height = imageSize.Height;
        if (pushConstants.IsMoving != 0 && pushConstants.PixelSizePercent > 100)
        {
            width = Math.Max(1, (int)(((long)width * 100L + pushConstants.PixelSizePercent - 1L) / pushConstants.PixelSizePercent));
            height = Math.Max(1, (int)(((long)height * 100L + pushConstants.PixelSizePercent - 1L) / pushConstants.PixelSizePercent));
        }

        return new PixelSize(width, height);
    }

    protected uint ComputeWavefrontQueueCapacity(PixelSize imageSize, PushDataGpu pushConstants)
    {
        var shadingSize = ComputeWavefrontShadingSize(imageSize, pushConstants);
        var sampleCount = (uint)GetWavefrontSamplesPerFrame(pushConstants);
        return (uint)Math.Max(shadingSize.Width * shadingSize.Height, 1) * Math.Max(sampleCount, 1u);
    }

    protected uint ComputeWavefrontWorkerCount(PixelSize imageSize, PushDataGpu pushConstants)
    {
        var shadingSize = ComputeWavefrontShadingSize(imageSize, pushConstants);
        var shadingPixelCount = (uint)Math.Max(shadingSize.Width * shadingSize.Height, 1);
        var desiredWorkerCount = Math.Min(shadingPixelCount, MaxWavefrontWorkers);
        return Math.Max(desiredWorkerCount, WavefrontQueueGroupSize);
    }

    protected int ComputeWavefrontDispatchBounceCount(PushDataGpu pushConstants)
    {
        var configuredMax = Math.Max(Scene.RenderSettings.PathDepth, 1);
        return Math.Max(configuredMax + 1, 1);
    }

    protected void ResetWavefrontCounters(CommandBuffer commandBuffer)
    {
        Context.Api.CmdFillBuffer(commandBuffer, WavefrontCountersBuffer.Handle, 0, WavefrontCountersBuffer.Size, 0);
        VulkanBarriers.Memory(
            Context.Api,
            commandBuffer,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.ComputeShaderBit);
    }

    protected void PushConstants(CommandBuffer commandBuffer, PipelineLayout pipelineLayout, PushDataGpu pushConstants)
    {
        Context.Api.CmdPushConstants(
            commandBuffer,
            pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)sizeof(PushDataGpu),
            &pushConstants);
    }

    protected void Dispatch2D(CommandBuffer commandBuffer, Pipeline pipeline, PipelineLayout pipelineLayout, PushDataGpu pushConstants, PixelSize size, uint groupSize)
    {
        Context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        PushConstants(commandBuffer, pipelineLayout, pushConstants);
        var width = (uint)Math.Max(1, size.Width);
        var height = (uint)Math.Max(1, size.Height);
        Context.Api.CmdDispatch(commandBuffer, (width + groupSize - 1) / groupSize, (height + groupSize - 1) / groupSize, 1);
    }

    protected void Dispatch1D(CommandBuffer commandBuffer, Pipeline pipeline, PipelineLayout pipelineLayout, PushDataGpu pushConstants, uint workItemCount)
    {
        Context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        PushConstants(commandBuffer, pipelineLayout, pushConstants);
        Context.Api.CmdDispatch(commandBuffer, Math.Max((workItemCount + WavefrontQueueGroupSize - 1) / WavefrontQueueGroupSize, 1u), 1, 1);
    }

    protected void DispatchSingle(CommandBuffer commandBuffer, Pipeline pipeline, PipelineLayout pipelineLayout, PushDataGpu pushConstants)
    {
        Context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        PushConstants(commandBuffer, pipelineLayout, pushConstants);
        Context.Api.CmdDispatch(commandBuffer, 1, 1, 1);
    }

    protected void InsertMemoryBarrier(CommandBuffer commandBuffer)
    {
        VulkanBarriers.Memory(
            Context.Api,
            commandBuffer,
            AccessFlags.ShaderWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit);
    }

    protected PipelineBundle CreatePipelineBundle(string prefix, string variantSuffix, PipelineLayout layout, ByteString mainName)
    {
        _ = mainName;
        const string entryPoint = "main";
        return new PipelineBundle(
            DeviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(Context, layout, $"Assets/Shaders/RayTracing/{prefix}Generate{variantSuffix}.spv", entryPoint)),
            DeviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(Context, layout, $"Assets/Shaders/RayTracing/{prefix}Extend{variantSuffix}.spv", entryPoint)),
            DeviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(Context, layout, $"Assets/Shaders/RayTracing/{prefix}Shade{variantSuffix}.spv", entryPoint)),
            DeviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(Context, layout, $"Assets/Shaders/RayTracing/{prefix}Connect{variantSuffix}.spv", entryPoint)),
            DeviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(Context, layout, $"Assets/Shaders/RayTracing/{prefix}Advance{variantSuffix}.spv", entryPoint)),
            DeviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(Context, layout, $"Assets/Shaders/RayTracing/{prefix}Finalize{variantSuffix}.spv", entryPoint)));
    }

    protected void ExecuteWavefrontPass(
        Context.CommandBuffer commandBuffer,
        VulkanImage image,
        PushDataGpu pushConstants,
        PipelineLayout pipelineLayout,
        DescriptorSet wavefrontBootstrapDescriptorSet,
        DescriptorSet wavefrontForwardDescriptorSet,
        DescriptorSet wavefrontReverseDescriptorSet,
        uint descriptorSetCount,
        DescriptorSet* boundDescriptorSets,
        PipelineBundle pipelines,
        string backendName)
    {
        EnsureWavefrontBuffers(
            commandBuffer,
            image.Size,
            pushConstants,
            wavefrontBootstrapDescriptorSet,
            wavefrontForwardDescriptorSet,
            wavefrontReverseDescriptorSet,
            backendName);

        var queueCapacity = ComputeWavefrontQueueCapacity(image.Size, pushConstants);
        var workerCount = ComputeWavefrontWorkerCount(image.Size, pushConstants);
        var maxBounces = ComputeWavefrontDispatchBounceCount(pushConstants);
        var usesQueuedShadowRays = EffectiveRenderMode is RenderMode.PathTracing;
        if (FrameIndex < 3)
        {
            Log.Debug(
                "{Backend} dispatch plan: frame={Frame} mode={RenderMode} size={Width}x{Height} queueCapacity={QueueCapacity} workerCount={WorkerCount} queuedShadows={QueuedShadows} maxBounces={MaxBounces}",
                backendName,
                FrameIndex,
                EffectiveRenderMode,
                image.Size.Width,
                image.Size.Height,
                queueCapacity,
                workerCount,
                usesQueuedShadowRays,
                maxBounces);
        }

        var vkCommandBuffer = commandBuffer.InternalHandle;
        ResetWavefrontCounters(vkCommandBuffer);
        boundDescriptorSets[1] = wavefrontBootstrapDescriptorSet;
        Context.Api.CmdBindDescriptorSets(vkCommandBuffer, PipelineBindPoint.Compute, pipelineLayout, 0, descriptorSetCount, boundDescriptorSets, 0, null);

        Dispatch2D(vkCommandBuffer, pipelines.Generate, pipelineLayout, pushConstants, ComputeWavefrontShadingSize(image.Size, pushConstants), 16);
        InsertMemoryBarrier(vkCommandBuffer);
        DispatchSingle(vkCommandBuffer, pipelines.Advance, pipelineLayout, pushConstants);
        InsertMemoryBarrier(vkCommandBuffer);

        var inputQueue = RayQueueABuffer;
        var outputQueue = RayQueueBBuffer;
        for (var bounce = 0; bounce < maxBounces; bounce++)
        {
            boundDescriptorSets[1] = inputQueue.Handle.Handle == RayQueueABuffer.Handle.Handle
                ? wavefrontForwardDescriptorSet
                : wavefrontReverseDescriptorSet;
            Context.Api.CmdBindDescriptorSets(vkCommandBuffer, PipelineBindPoint.Compute, pipelineLayout, 0, descriptorSetCount, boundDescriptorSets, 0, null);

            Dispatch1D(vkCommandBuffer, pipelines.Extend, pipelineLayout, pushConstants, workerCount);
            InsertMemoryBarrier(vkCommandBuffer);

            Dispatch1D(vkCommandBuffer, pipelines.Shade, pipelineLayout, pushConstants, workerCount);
            InsertMemoryBarrier(vkCommandBuffer);

            if (usesQueuedShadowRays)
            {
                Dispatch1D(vkCommandBuffer, pipelines.Connect, pipelineLayout, pushConstants, workerCount);
                InsertMemoryBarrier(vkCommandBuffer);
            }

            DispatchSingle(vkCommandBuffer, pipelines.Advance, pipelineLayout, pushConstants);
            InsertMemoryBarrier(vkCommandBuffer);

            (inputQueue, outputQueue) = (outputQueue, inputQueue);
        }

        boundDescriptorSets[1] = inputQueue.Handle.Handle == RayQueueABuffer.Handle.Handle
            ? wavefrontForwardDescriptorSet
            : wavefrontReverseDescriptorSet;
        Context.Api.CmdBindDescriptorSets(vkCommandBuffer, PipelineBindPoint.Compute, pipelineLayout, 0, descriptorSetCount, boundDescriptorSets, 0, null);
        Dispatch2D(vkCommandBuffer, pipelines.Finalize, pipelineLayout, pushConstants, ComputeWavefrontShadingSize(image.Size, pushConstants), 16);
    }

    protected void DisposeWavefrontResources()
    {
        ShadowQueueBuffer.Dispose();
        HitQueueBuffer.Dispose();
        RayQueueBBuffer.Dispose();
        RayQueueABuffer.Dispose();
        PathStateBuffer.Dispose();
        WavefrontCountersBuffer.Dispose();
    }

    protected VulkanBuffer UploadDeviceLocalBuffer(Context.CommandBuffer commandBuffer, ReadOnlySpan<byte> data, BufferUsageFlags usage)
    {
        var destinationBuffer = CreateDeviceLocalBuffer((ulong)data.Length, usage);
        var stagingBuffer = new VulkanBuffer(
            Context,
            (ulong)data.Length,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            data);
        CopyBuffer(commandBuffer.InternalHandle, stagingBuffer, destinationBuffer, (ulong)data.Length);
        Context.RetainForExecution(commandBuffer, stagingBuffer);
        return destinationBuffer;
    }

    private void CopyBuffer(CommandBuffer commandBuffer, VulkanBuffer sourceBuffer, VulkanBuffer destinationBuffer, ulong size)
    {
        var region = new BufferCopy
        {
            Size = size
        };
        Context.Api.CmdCopyBuffer(commandBuffer, sourceBuffer.Handle, destinationBuffer.Handle, 1, in region);
    }

    protected void InsertTransferToAccelerationStructureReadBarrier(CommandBuffer commandBuffer)
    {
        VulkanBarriers.Memory(
            Context.Api,
            commandBuffer,
            AccessFlags.TransferWriteBit,
            AccessFlags.AccelerationStructureReadBitKhr,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.AccelerationStructureBuildBitKhr);
    }

    private uint ReadPixelUInt(VulkanImage image, int pixelX, int pixelY)
    {
        using var staging = new VulkanBuffer(
            Context,
            sizeof(uint),
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        CopyImagePixelToBuffer(image, staging, pixelX, pixelY);

        unsafe
        {
            void* mapped = null;
            Context.Api.MapMemory(Context.Device, staging.Memory, 0, staging.Size, 0, &mapped).ThrowOnError();
            try
            {
                return *(uint*)mapped;
            }
            finally
            {
                Context.Api.UnmapMemory(Context.Device, staging.Memory);
            }
        }
    }

    private Vector4 ReadPixelHalf4(VulkanImage image, int pixelX, int pixelY)
    {
        using var staging = new VulkanBuffer(
            Context,
            sizeof(ushort) * 4,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        CopyImagePixelToBuffer(image, staging, pixelX, pixelY);

        var bytes = new byte[sizeof(ushort) * 4];
        unsafe
        {
            void* mapped = null;
            Context.Api.MapMemory(Context.Device, staging.Memory, 0, staging.Size, 0, &mapped).ThrowOnError();
            try
            {
                Marshal.Copy((nint)mapped, bytes, 0, bytes.Length);
            }
            finally
            {
                Context.Api.UnmapMemory(Context.Device, staging.Memory);
            }
        }

        var x = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, 0));
        var y = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, 2));
        var z = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, 4));
        var w = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, 6));
        return new Vector4(x, y, z, w);
    }

    private void CopyImagePixelToBuffer(VulkanImage image, VulkanBuffer stagingBuffer, int pixelX, int pixelY)
    {
        Context.WaitForSubmittedCommandBuffers();

        var commandBuffer = Context.CreateCommandBuffer();
        Context.BeginCommandBuffer(commandBuffer);

        image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);

        var copyRegion = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(pixelX, pixelY, 0),
            ImageExtent = new Extent3D(1, 1, 1)
        };

        Context.Api.CmdCopyImageToBuffer(
            commandBuffer.InternalHandle,
            image.InternalHandle,
            ImageLayout.TransferSrcOptimal,
            stagingBuffer.Handle,
            1,
            in copyRegion);

        image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        Context.SubmitAndWait(commandBuffer);
    }

    protected static string[] GetSupportedHandleTypes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return [KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle];

        return [KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor];
    }

    protected void DisposeCommonResources()
    {
        DisposeRenderImages();
        sceneSettingsUploadBuffer?.Dispose();
        sceneSettingsUploadBuffer = null;
        sceneSettingsBuffer?.Dispose();
        sceneSettingsBuffer = null;
    }

    public abstract void Dispose();
}
