using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

internal abstract unsafe class GpuRaytracer : IRaytracer
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

    public ImageResource OutputColor => outputColorImage ?? throw new InvalidOperationException("Raytracer output image is not initialized");

    public void Render(ImageResource image)
    {
        EnsureRenderImages(image.Size);
        UpdateSceneResources(force: false);
        UpdateOutputImageBindings();
        UpdateTextureBindings();

        if (Scene.IsDirty(SceneDirtyFlags.Accumulation))
            FrameIndex = 0;

        if (!hasLoggedFirstRender || FrameIndex < 3)
        {
            Log.Information(
                "{RaytracerType} frame={Frame} size={Width}x{Height} envTex={EnvironmentTextureIndex} sceneVersion={SceneVersion}",
                GetType().Name,
                FrameIndex,
                image.Size.Width,
                image.Size.Height,
                Scene.Environment.TextureIndex,
                Scene.Version);
            hasLoggedFirstRender = true;
        }

        var commandBuffer = Context.Pool.CreateCommandBuffer();
        commandBuffer.BeginRecording();

        outputColorImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        albedoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        normalImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        cryptoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        positionImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        ExecuteRaytracing(commandBuffer.InternalHandle, outputColorImage, CreatePushConstants(FrameIndex));
        commandBuffer.Submit();

        FrameIndex++;
        Scene.ClearDirty(SceneDirtyFlags.Accumulation);
    }

    protected abstract void ExecuteRaytracing(CommandBuffer commandBuffer, ImageResource image, PushConstantsDataGpu pushConstants);
    protected abstract void UpdateSceneResources(bool force);
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

    protected PushConstantsDataGpu CreatePushConstants(uint frame)
    {
        return new PushConstantsDataGpu
        {
            Push = new PushDataGpu
            {
                Frame = (int)frame,
                Samples = 1,
                DiffuseBounces = 2,
                SpecularBounces = 2,
                TransmissionBounces = 2,
                Exposure = 0f,
                IsMoving = Scene.CameraIsMoving,
                VisualizeBvh = 0
            },
            Camera = Scene.Camera,
            Environment = Scene.Environment
        };
    }

    protected static string[] GetSupportedHandleTypes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return [KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle];

        return [KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor];
    }

    public abstract void Dispose();
}
