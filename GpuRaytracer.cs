using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

internal abstract unsafe class GpuRaytracer : IDisposable
{
    protected const uint MaxTextures = 10_000;

    protected readonly Context Context;
    protected readonly Scene Scene;

    private ImageResource? outputColorImage;
    private ImageResource? albedoImage;
    private ImageResource? normalImage;
    private ImageResource? cryptoImage;
    private ImageResource? positionImage;
    private PixelSize renderImageSize;

    private ulong lastBoundColorImageViewHandle;
    protected uint FrameIndex;
    private bool hasLoggedFirstRender;

    protected GpuRaytracer(Context context, Scene scene)
    {
        Context = context;
        Scene = scene;
    }

    public static GpuRaytracer Create(Context context, Scene scene)
    {
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
    }

    public ImageResource OutputColor => outputColorImage ?? throw new InvalidOperationException("Raytracer output image is not initialized");
    public ImageResource OutputCrypto => cryptoImage ?? throw new InvalidOperationException("Raytracer crypto image is not initialized");
    public ImageResource OutputPosition => positionImage ?? throw new InvalidOperationException("Raytracer position image is not initialized");
    public PixelSize RenderImageSize => renderImageSize;

    public void Record(ImageResource image, CommandBufferPool.PooledCommandBuffer commandBuffer)
    {
        EnsureRenderImages(image.Size);
        UpdateSceneResources(commandBuffer, force: false);
        UpdateOutputImageBindings();
        UpdateTextureBindings();

        if (Scene.IsDirty(SceneDirtyFlags.Accumulation))
            FrameIndex = 0;

        if (!hasLoggedFirstRender || FrameIndex < 3)
        {
            Log.Debug(
                "{RaytracerType} frame={Frame} size={Width}x{Height} envTex={EnvironmentTextureIndex} sceneVersion={SceneVersion}",
                GetType().Name,
                FrameIndex,
                image.Size.Width,
                image.Size.Height,
                Scene.Environment.TextureIndex,
                Scene.Version);
            hasLoggedFirstRender = true;
        }

        outputColorImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        albedoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        normalImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        cryptoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        positionImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        ExecuteRaytracing(commandBuffer.InternalHandle, outputColorImage, CreatePushConstants(FrameIndex));

        FrameIndex++;
        Scene.ClearDirty(SceneDirtyFlags.Accumulation);
    }

    protected abstract void ExecuteRaytracing(CommandBuffer commandBuffer, ImageResource image, PushConstantsDataGpu pushConstants);
    protected abstract void UpdateSceneResources(CommandBufferPool.PooledCommandBuffer commandBuffer, bool force);
    protected abstract DescriptorSet GetDescriptorSet();

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

        var writes = stackalloc WriteDescriptorSet[5];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &colorInfo
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &albedoInfo
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 3,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &normalInfo
        };
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 4,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &cryptoInfo
        };
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 5,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &positionInfo
        };

        Context.Api.UpdateDescriptorSets(Context.Device, 5, writes, 0, null);
        lastBoundColorImageViewHandle = outputColorImage.ViewHandle;
        Log.Information("Updated output image bindings (color/albedo/normal/crypto/position).");
    }

    private void UpdateTextureBindings()
    {
        if (!Scene.IsDirty(SceneDirtyFlags.Textures))
            return;

        var textures = Scene.Textures;
        if (textures.Count > MaxTextures)
            throw new InvalidOperationException($"Too many textures for bindless descriptor array ({textures.Count} > {MaxTextures}).");

        if (textures.Count == 0)
        {
            Log.Information("No textures bound for bindless descriptor array.");
            Scene.ClearDirty(SceneDirtyFlags.Textures);
            return;
        }

        var descriptors = new DescriptorImageInfo[textures.Count];
        for (var i = 0; i < textures.Count; i++)
            descriptors[i] = textures[i].GetDescriptorImageInfo();

        fixed (DescriptorImageInfo* pDescriptors = descriptors)
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = GetDescriptorSet(),
                DstBinding = 7,
                DescriptorCount = (uint)descriptors.Length,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = pDescriptors
            };
            Context.Api.UpdateDescriptorSets(Context.Device, 1, in write, 0, null);
        }

        Scene.ClearDirty(SceneDirtyFlags.Textures);
        Log.Information("Updated bindless texture descriptors: count={TextureCount}", descriptors.Length);
    }

    private void EnsureRenderImages(PixelSize size)
    {
        if (outputColorImage is not null && renderImageSize == size)
            return;

        outputColorImage?.Dispose();
        albedoImage?.Dispose();
        normalImage?.Dispose();
        cryptoImage?.Dispose();
        positionImage?.Dispose();

        var supportedHandles = GetSupportedHandleTypes();
        outputColorImage = new ImageResource(Context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        albedoImage = new ImageResource(Context, (uint)Format.R8G8B8A8Unorm, size, false, supportedHandles);
        normalImage = new ImageResource(Context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        cryptoImage = new ImageResource(Context, (uint)Format.R32Uint, size, false, supportedHandles);
        positionImage = new ImageResource(Context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        renderImageSize = size;
        lastBoundColorImageViewHandle = 0;
        FrameIndex = 0;
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
        renderImageSize = default;
        lastBoundColorImageViewHandle = 0;
    }

    public bool QueryPixelUInt(ImageResource image, int pixelX, int pixelY, out uint value)
    {
        value = 0;
        if (image is null)
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

        var maxX = renderImageSize.Width - 1;
        var maxY = renderImageSize.Height - 1;
        if (pixelX < 0 || pixelY < 0 || pixelX > maxX || pixelY > maxY)
            return false;

        value = ReadPixelHalf4(image, pixelX, pixelY);
        return true;
    }

    protected PushConstantsDataGpu CreatePushConstants(uint frame)
    {
        var push = new PushDataGpu
        {
            Frame = (int)frame,
            IsMoving = Scene.CameraIsMoving
        };

        return new PushConstantsDataGpu
        {
            Push = push,
            Camera = Scene.Camera,
            Environment = Scene.Environment
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
        var commandBuffer = Context.Pool.CreateCommandBuffer();
        commandBuffer.BeginRecording();

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
        commandBuffer.SubmitAndWait();
    }

    protected static string[] GetSupportedHandleTypes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return [KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle];

        return [KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor];
    }

    public abstract void Dispose();
}
