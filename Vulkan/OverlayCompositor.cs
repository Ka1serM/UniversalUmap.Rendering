using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Vulkan;

public sealed unsafe class Compositor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CompositePushConstants
    {
        public uint SelectedInstanceId;
        public int VisualizationMode;
        public float AdaptiveTargetError;
        public int AdaptiveMinSamples;
        public int IsMoving;
        public int Pad0;  // Struct padding to 16 bytes
    }

    private readonly Context context;
    private readonly VulkanDeviceResources deviceResources;
    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly PipelineLayout pipelineLayout;
    private readonly Pipeline pipeline;
    private readonly Dictionary<ulong, DescriptorSet> descriptorSetsByOutputViewHandle = [];
    private ulong lastColorInputViewHandle;
    private ulong lastAlbedoInputViewHandle;
    private ulong lastNormalInputViewHandle;
    private ulong lastCryptoInputViewHandle;
    private ulong lastPositionInputViewHandle;
    private ulong lastAdaptiveInputViewHandle;
    private bool hasLoggedDispatch;

    public Compositor(Context context)
    {
        this.context = context;
        deviceResources = new VulkanDeviceResources(context);
        var descriptorSetLayoutLocal = deviceResources.Track(new VulkanDescriptorSetBuilder()
            .Add(0, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(6, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .BuildLayout(context));

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)sizeof(CompositePushConstants)
        };
        Span<DescriptorSetLayout> setLayouts = stackalloc DescriptorSetLayout[1];
        setLayouts[0] = descriptorSetLayoutLocal;
        var pipelineLayoutLocal = deviceResources.Track(VulkanPipelineFactory.CreatePipelineLayout(context, setLayouts, pushConstantRange));
        var pipelineLocal = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, pipelineLayoutLocal, "Assets/Shaders/Compositing/Compositor.spv"));

        descriptorSetLayout = descriptorSetLayoutLocal;
        pipelineLayout = pipelineLayoutLocal;
        pipeline = pipelineLocal;
        Log.Information("Compositor pipeline created.");
    }

    public void Record(
        Context.CommandBuffer commandBuffer,
        VulkanImage colorInputImage,
        VulkanImage albedoInputImage,
        VulkanImage normalInputImage,
        VulkanImage cryptoInputImage,
        VulkanImage positionInputImage,
        VulkanImage adaptiveInputImage,
        int visualizationMode,
        float adaptiveTargetError,
        int adaptiveMinSamples,
        uint selectedInstanceId,
        VulkanImage outputImage,
        int isMoving)
    {
        var descriptorSet = UpdateBindings(colorInputImage, albedoInputImage, normalInputImage, cryptoInputImage, positionInputImage, adaptiveInputImage, outputImage);

        colorInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        albedoInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        normalInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        cryptoInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        positionInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        adaptiveInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        outputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderWriteBit);

        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Compute, pipeline);
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Compute, pipelineLayout, 0, 1, in descriptorSet, 0, null);
        var pushConstants = new CompositePushConstants
        {
            SelectedInstanceId = selectedInstanceId,
            VisualizationMode = visualizationMode,
            AdaptiveTargetError = adaptiveTargetError,
            AdaptiveMinSamples = adaptiveMinSamples,
            IsMoving = isMoving,
        };
        context.Api.CmdPushConstants(
            commandBuffer.InternalHandle,
            pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)sizeof(CompositePushConstants),
            &pushConstants);

        const uint groupSize = 16;
        var width = (uint)Math.Max(1, outputImage.Size.Width);
        var height = (uint)Math.Max(1, outputImage.Size.Height);
        var groupCountX = (width + groupSize - 1) / groupSize;
        var groupCountY = (height + groupSize - 1) / groupSize;
        if (!hasLoggedDispatch)
        {
            Log.Information("Compositor dispatch: {GroupCountX}x{GroupCountY} groups for {Width}x{Height}.", groupCountX, groupCountY, width, height);
            hasLoggedDispatch = true;
        }
        context.Api.CmdDispatch(commandBuffer.InternalHandle, groupCountX, groupCountY, 1);
    }

    private DescriptorSet UpdateBindings(
        VulkanImage colorInputImage,
        VulkanImage albedoInputImage,
        VulkanImage normalInputImage,
        VulkanImage cryptoInputImage,
        VulkanImage positionInputImage,
        VulkanImage adaptiveInputImage,
        VulkanImage outputImage)
    {
        var inputsChanged =
            lastColorInputViewHandle != colorInputImage.ViewHandle ||
            lastAlbedoInputViewHandle != albedoInputImage.ViewHandle ||
            lastNormalInputViewHandle != normalInputImage.ViewHandle ||
            lastCryptoInputViewHandle != cryptoInputImage.ViewHandle ||
            lastPositionInputViewHandle != positionInputImage.ViewHandle ||
            lastAdaptiveInputViewHandle != adaptiveInputImage.ViewHandle;

        if (inputsChanged)
        {
            ResetDescriptorCache();
            lastColorInputViewHandle = colorInputImage.ViewHandle;
            lastAlbedoInputViewHandle = albedoInputImage.ViewHandle;
            lastNormalInputViewHandle = normalInputImage.ViewHandle;
            lastCryptoInputViewHandle = cryptoInputImage.ViewHandle;
            lastPositionInputViewHandle = positionInputImage.ViewHandle;
            lastAdaptiveInputViewHandle = adaptiveInputImage.ViewHandle;
            Log.Debug("Compositor input bindings changed; invalidating cached output descriptor sets.");
        }

        if (descriptorSetsByOutputViewHandle.TryGetValue(outputImage.ViewHandle, out var cachedDescriptorSet))
            return cachedDescriptorSet;

        var descriptorSet = VulkanDescriptorSet.Allocate(context, descriptorSetLayout);
        new VulkanDescriptorWriter()
            .StorageImage(0, colorInputImage)
            .StorageImage(1, albedoInputImage)
            .StorageImage(2, normalInputImage)
            .StorageImage(3, cryptoInputImage)
            .StorageImage(4, positionInputImage)
            .StorageImage(5, adaptiveInputImage)
            .StorageImage(6, outputImage)
            .Update(context, descriptorSet);
        descriptorSetsByOutputViewHandle[outputImage.ViewHandle] = descriptorSet;
        Log.Debug("Compositor descriptor set created for output view {OutputViewHandle}.", outputImage.ViewHandle);
        return descriptorSet;
    }

    private void ResetDescriptorCache()
    {
        if (descriptorSetsByOutputViewHandle.Count == 0)
            return;

        context.WaitForSubmittedCommandBuffers();
        var descriptorSets = descriptorSetsByOutputViewHandle.Values.ToArray();
        VulkanDescriptorSet.Free(context, descriptorSets);
        descriptorSetsByOutputViewHandle.Clear();
    }

    public void Dispose()
    {
        ResetDescriptorCache();
        deviceResources.Dispose();
    }
}
