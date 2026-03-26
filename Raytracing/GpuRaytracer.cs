using System;
using System.Numerics;
using System.Runtime.InteropServices;
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
    public int ShaderPixelSizePercent { get; set; } = 100;

    protected readonly Context Context;
    protected readonly Scene Scene;
    protected RenderMode CachedRenderMode;
    private bool hasLoggedCurrentRenderMode;
    private bool settingsDirty;

    private ImageResource? outputColorImage;
    private ImageResource? albedoImage;
    private ImageResource? normalImage;
    private ImageResource? cryptoImage;
    private ImageResource? positionImage;
    private ImageResource? adaptiveStateImage;
    private GpuBuffer? sceneSettingsBuffer;
    private PixelSize renderImageSize;

    private ulong lastBoundColorImageViewHandle;
    protected uint FrameIndex;
    private bool hasLoggedFirstRender;
    private Context.CommandBuffer? pendingResizeCompletion;
    private bool hasDeferredResize;
    private PixelSize deferredResizeSize;

    private SceneSettingsDataGpu lastUploadedSettings;

    protected GpuRaytracer(Context context, Scene scene)
    {
        Context = context;
        Scene = scene;
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

    public static GpuRaytracer Create(Context context, Scene scene)
    {
#if DISABLE_RTX
        Log.Information("RTX backend compile-time disabled (DISABLE_RTX); using Compute raytracer backend.");
        return new ComputeRaytracer(context, scene);
#else
        if (!context.RayTracingSupported)
        {
            Log.Information("Hardware ray tracing unsupported; using Compute raytracer backend.");
            return new ComputeRaytracer(context, scene);
        }

        try
        {
            var rtxRaytracer = new RtxRaytracer(context, scene);
            Log.Information("Using RTX raytracer backend.");
            return rtxRaytracer;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RTX raytracer creation failed, falling back to compute.");
            var computeRaytracer = new ComputeRaytracer(context, scene);
            Log.Information("Using Compute raytracer backend.");
            return computeRaytracer;
        }
#endif
    }

    public ImageResource OutputColor => outputColorImage ?? throw new InvalidOperationException("Raytracer output image is not initialized");
    public ImageResource OutputAlbedo => albedoImage ?? throw new InvalidOperationException("Raytracer albedo image is not initialized");
    public ImageResource OutputNormal => normalImage ?? throw new InvalidOperationException("Raytracer normal image is not initialized");
    public ImageResource OutputCrypto => cryptoImage ?? throw new InvalidOperationException("Raytracer crypto image is not initialized");
    public ImageResource OutputPosition => positionImage ?? throw new InvalidOperationException("Raytracer position image is not initialized");
    public ImageResource OutputAdaptiveState => adaptiveStateImage ?? throw new InvalidOperationException("Raytracer adaptive-state image is not initialized");
    public PixelSize RenderImageSize => renderImageSize;
    public bool PickBuffersFlippedY => true;

    public void Record(PixelSize renderSize, ImageResource image, Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData)
    {
        CompletePendingResizeIfReady();
        EnsureRenderImages(renderSize, commandBuffer);
        UpdateSceneResources(commandBuffer, force: false);
        UpdateOutputImageBindings();
        UpdateSceneSettingsBuffer(commandBuffer, renderData, force: false);
        UpdateTextureBindings();

        if (Scene.IsDirty(SceneDirtyFlags.Accumulation))
            FrameIndex = 0;

        if (!hasLoggedFirstRender || FrameIndex < 3)
        {
            Log.Debug(
                "{RaytracerType} frame={Frame} size={Width}x{Height} envTex={EnvironmentTextureIndex}",
                GetType().Name,
                FrameIndex,
                renderSize.Width,
                renderSize.Height,
                renderData.EnvironmentTextureIndex);
            hasLoggedFirstRender = true;
        }

        if (!hasLoggedCurrentRenderMode)
        {
            Log.Information("Raytracer using {RenderMode} mode.", CachedRenderMode);
            hasLoggedCurrentRenderMode = true;
        }

        outputColorImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        albedoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        normalImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        cryptoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        positionImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        adaptiveStateImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        ExecuteRaytracing(commandBuffer.InternalHandle, outputColorImage, CreatePushConstants(FrameIndex, renderData));

        FrameIndex++;
        Scene.ClearDirty(SceneDirtyFlags.Accumulation);
    }

    protected abstract void ExecuteRaytracing(CommandBuffer commandBuffer, ImageResource image, PushDataGpu pushConstants);
    protected abstract void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force);
    protected abstract DescriptorSet GetDescriptorSet();
    protected abstract DescriptorSetLayout GetDescriptorSetLayout();
    protected virtual uint OutputImageBindingBase => 1;
    protected virtual uint SceneSettingsBinding => 8;
    protected virtual uint TextureArrayBinding => 9;
    protected virtual uint RtxInstanceBufferBinding => 1; // Only used by RTX path

    protected void UpdateOutputImageBindings()
    {
        if (outputColorImage is null)
            throw new InvalidOperationException("Raytracer output images are not initialized.");

        if (lastBoundColorImageViewHandle == outputColorImage.ViewHandle)
            return;

        var descriptorSet = GetDescriptorSet();

        var colorInfo = new DescriptorImageInfo(default, new ImageView(outputColorImage.ViewHandle), ImageLayout.General);
        var albedoInfo = new DescriptorImageInfo(default, new ImageView(albedoImage!.ViewHandle), ImageLayout.General);
        var normalInfo = new DescriptorImageInfo(default, new ImageView(normalImage!.ViewHandle), ImageLayout.General);
        var cryptoInfo = new DescriptorImageInfo(default, new ImageView(cryptoImage!.ViewHandle), ImageLayout.General);
        var positionInfo = new DescriptorImageInfo(default, new ImageView(positionImage!.ViewHandle), ImageLayout.General);
        var adaptiveStateInfo = new DescriptorImageInfo(default, new ImageView(adaptiveStateImage!.ViewHandle), ImageLayout.General);

        var writes = stackalloc WriteDescriptorSet[6];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = OutputImageBindingBase + 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &colorInfo
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = OutputImageBindingBase + 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &albedoInfo
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = OutputImageBindingBase + 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &normalInfo
        };
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = OutputImageBindingBase + 3,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &cryptoInfo
        };
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = OutputImageBindingBase + 4,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &positionInfo
        };
        writes[5] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = OutputImageBindingBase + 5,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &adaptiveStateInfo
        };

        Context.Api.UpdateDescriptorSets(Context.Device, 6, writes, 0, null);
        lastBoundColorImageViewHandle = outputColorImage.ViewHandle;
        Log.Debug("Updated output image bindings (color/albedo/normal/crypto/position/adaptive-state).");
    }

    private bool UpdateSceneSettingsBinding()
    {
        if (sceneSettingsBuffer is not null)
            return false;

        sceneSettingsBuffer = new GpuBuffer(
            Context,
            (ulong)Marshal.SizeOf<SceneSettingsDataGpu>(),
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        var settingsInfo = new DescriptorBufferInfo(sceneSettingsBuffer.Handle, 0, sceneSettingsBuffer.Size);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = GetDescriptorSet(),
            DstBinding = SceneSettingsBinding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &settingsInfo
        };
        Context.Api.UpdateDescriptorSets(Context.Device, 1, in write, 0, null);
        return true;
    }

    private void UpdateSceneSettingsBuffer(Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData, bool force)
    {
        var created = UpdateSceneSettingsBinding();
        if (sceneSettingsBuffer is null)
            throw new InvalidOperationException("Scene settings buffer is not initialized.");

        if (!force && !created && !settingsDirty)
            return;

        var settings = new SceneSettingsDataGpu
        {
            RenderSettings = renderData.RenderSettings,
            Environment = renderData.Environment,
            RasterCamera = Scene.CaptureRasterCameraData()
        };

        sceneSettingsBuffer.Upload(StructPacking.ToBytes(new[] { settings }));
        lastUploadedSettings = settings;
        settingsDirty = false;
    }

    private void UpdateTextureBindings()
    {
        if (!Scene.IsDirty(SceneDirtyFlags.Textures))
            return;

        var textures = Scene.GetTexturesSnapshot();
        if (textures.Length > MaxTextures)
            throw new InvalidOperationException($"Too many textures for bindless descriptor array ({textures.Length} > {MaxTextures}).");

        if (textures.Length == 0)
        {
            Log.Information("No textures bound for bindless descriptor array.");
            Scene.ClearDirty(SceneDirtyFlags.Textures);
            return;
        }

        var descriptors = new DescriptorImageInfo[textures.Length];
        for (var i = 0; i < textures.Length; i++)
            descriptors[i] = textures[i].GetDescriptorImageInfo();

        fixed (DescriptorImageInfo* pDescriptors = descriptors)
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = GetDescriptorSet(),
                DstBinding = TextureArrayBinding,
                DescriptorCount = (uint)descriptors.Length,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = pDescriptors
            };
            Context.Api.UpdateDescriptorSets(Context.Device, 1, in write, 0, null);
        }

        Scene.ClearDirty(SceneDirtyFlags.Textures);
        Log.Debug("Updated bindless texture descriptors: count={TextureCount}", descriptors.Length);
    }

    private void EnsureRenderImages(PixelSize size, Context.CommandBuffer commandBuffer)
    {
        if (outputColorImage is not null && renderImageSize == size)
            return;

        if (pendingResizeCompletion is not null)
        {
            // Keep only the latest requested size while a previous resize is still in flight.
            hasDeferredResize = true;
            deferredResizeSize = size;
            return;
        }

        if (hasDeferredResize)
        {
            size = deferredResizeSize;
            hasDeferredResize = false;
        }

        var previousOutputColorImage = outputColorImage;
        var previousAlbedoImage = albedoImage;
        var previousNormalImage = normalImage;
        var previousCryptoImage = cryptoImage;
        var previousPositionImage = positionImage;
        var previousAdaptiveStateImage = adaptiveStateImage;

        if (previousOutputColorImage is not null)
            Context.RetainForExecution(commandBuffer, previousOutputColorImage);
        if (previousAlbedoImage is not null)
            Context.RetainForExecution(commandBuffer, previousAlbedoImage);
        if (previousNormalImage is not null)
            Context.RetainForExecution(commandBuffer, previousNormalImage);
        if (previousCryptoImage is not null)
            Context.RetainForExecution(commandBuffer, previousCryptoImage);
        if (previousPositionImage is not null)
            Context.RetainForExecution(commandBuffer, previousPositionImage);
        if (previousAdaptiveStateImage is not null)
            Context.RetainForExecution(commandBuffer, previousAdaptiveStateImage);

        var supportedHandles = GetSupportedHandleTypes();
        outputColorImage = new ImageResource(Context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        albedoImage = new ImageResource(Context, (uint)Format.R8G8B8A8Unorm, size, false, supportedHandles);
        normalImage = new ImageResource(Context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        cryptoImage = new ImageResource(Context, (uint)Format.R32Uint, size, false, supportedHandles);
        positionImage = new ImageResource(Context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        adaptiveStateImage = new ImageResource(Context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        renderImageSize = size;
        lastBoundColorImageViewHandle = 0;
        FrameIndex = 0;
        commandBuffer.AddExternalReference();
        pendingResizeCompletion = commandBuffer;
        Log.Information("Recreated raytracer intermediate images: {Width}x{Height}", size.Width, size.Height);
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

    public bool QueryPixelUInt(ImageResource image, int pixelX, int pixelY, out uint value)
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

    public bool QueryPixelHalf4(ImageResource image, int pixelX, int pixelY, out Vector4 value)
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

    private uint ReadPixelUInt(ImageResource image, int pixelX, int pixelY)
    {
        using var staging = new GpuBuffer(
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

    private Vector4 ReadPixelHalf4(ImageResource image, int pixelX, int pixelY)
    {
        using var staging = new GpuBuffer(
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

    private void CopyImagePixelToBuffer(ImageResource image, GpuBuffer stagingBuffer, int pixelX, int pixelY)
    {
        // Picking is latency-sensitive but infrequent. Drain older submissions first so
        // the readback runs against a fully produced image instead of racing startup or resize work.
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
        ReleasePendingResizeTracker(waitForCompletion: true);
        DisposeRenderImages();
        sceneSettingsBuffer?.Dispose();
        sceneSettingsBuffer = null;
    }

    private void CompletePendingResizeIfReady()
    {
        if (pendingResizeCompletion is null)
            return;

        if (!pendingResizeCompletion.IsExecutionComplete())
            return;

        pendingResizeCompletion.ReleaseExternalReference();
        pendingResizeCompletion = null;
    }

    private void ReleasePendingResizeTracker(bool waitForCompletion)
    {
        if (pendingResizeCompletion is null)
            return;

        try
        {
            if (waitForCompletion)
                pendingResizeCompletion.WaitForCompletion();
        }
        finally
        {
            pendingResizeCompletion.ReleaseExternalReference();
            pendingResizeCompletion = null;
            hasDeferredResize = false;
            deferredResizeSize = default;
        }
    }

    public abstract void Dispose();
}
