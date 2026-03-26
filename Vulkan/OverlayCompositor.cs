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
        public int PickBuffersFlippedY;
        public int Pad0;  // Struct padding to 16 bytes
        public int Pad1;  // Struct padding to 16 bytes
    }

    private readonly Context context;
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
        var shaderBytes = EmbeddedAssets.ReadByFileName("Assets/Shaders/Compositing/Compositor.spv");
        using var mainName = new ByteString("main");

        ShaderModule computeModule;
        fixed (byte* pShader = shaderBytes)
        {
            var shaderInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)shaderBytes.Length,
                PCode = (uint*)pShader
            };
            context.Api.CreateShaderModule(context.Device, in shaderInfo, default, out computeModule).ThrowOnError();
        }

        var layoutBindings = stackalloc DescriptorSetLayoutBinding[7];
        layoutBindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[3] = new DescriptorSetLayoutBinding(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[4] = new DescriptorSetLayoutBinding(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[5] = new DescriptorSetLayoutBinding(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[6] = new DescriptorSetLayoutBinding(6, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);

        var descriptorSetLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 7,
            PBindings = layoutBindings
        };
        context.Api.CreateDescriptorSetLayout(context.Device, in descriptorSetLayoutInfo, default, out var descriptorSetLayoutLocal).ThrowOnError();

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayoutLocal
        };
        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)sizeof(CompositePushConstants)
        };
        pipelineLayoutInfo.PushConstantRangeCount = 1;
        pipelineLayoutInfo.PPushConstantRanges = &pushConstantRange;
        context.Api.CreatePipelineLayout(context.Device, in pipelineLayoutInfo, default, out var pipelineLayoutLocal).ThrowOnError();

        var stageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = computeModule,
            PName = mainName
        };
        var pipelineInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stageInfo,
            Layout = pipelineLayoutLocal
        };
        context.Api.CreateComputePipelines(context.Device, default, 1, in pipelineInfo, default, out var pipelineLocal).ThrowOnError();
        context.Api.DestroyShaderModule(context.Device, computeModule, default);

        descriptorSetLayout = descriptorSetLayoutLocal;
        pipelineLayout = pipelineLayoutLocal;
        pipeline = pipelineLocal;
        Log.Information("Compositor pipeline created.");
    }

    public void Record(
        Context.CommandBuffer commandBuffer,
        ImageResource colorInputImage,
        ImageResource albedoInputImage,
        ImageResource normalInputImage,
        ImageResource cryptoInputImage,
        ImageResource positionInputImage,
        ImageResource adaptiveInputImage,
        int visualizationMode,
        float adaptiveTargetError,
        int adaptiveMinSamples,
        uint selectedInstanceId,
        ImageResource outputImage,
        int isMoving,
        bool pickBuffersFlippedY)
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
            PickBuffersFlippedY = pickBuffersFlippedY ? 1 : 0
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
        ImageResource colorInputImage,
        ImageResource albedoInputImage,
        ImageResource normalInputImage,
        ImageResource cryptoInputImage,
        ImageResource positionInputImage,
        ImageResource adaptiveInputImage,
        ImageResource outputImage)
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

        var descriptorSet = AllocateDescriptorSet();

        var colorInputInfo = new DescriptorImageInfo(default, new ImageView(colorInputImage.ViewHandle), ImageLayout.General);
        var albedoInputInfo = new DescriptorImageInfo(default, new ImageView(albedoInputImage.ViewHandle), ImageLayout.General);
        var normalInputInfo = new DescriptorImageInfo(default, new ImageView(normalInputImage.ViewHandle), ImageLayout.General);
        var cryptoInputInfo = new DescriptorImageInfo(default, new ImageView(cryptoInputImage.ViewHandle), ImageLayout.General);
        var positionInputInfo = new DescriptorImageInfo(default, new ImageView(positionInputImage.ViewHandle), ImageLayout.General);
        var adaptiveInputInfo = new DescriptorImageInfo(default, new ImageView(adaptiveInputImage.ViewHandle), ImageLayout.General);
        var outputInfo = new DescriptorImageInfo(default, new ImageView(outputImage.ViewHandle), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &colorInputInfo
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &albedoInputInfo
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &normalInputInfo
        };
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 3,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &cryptoInputInfo
        };
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 4,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &positionInputInfo
        };
        writes[5] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 5,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &adaptiveInputInfo
        };
        writes[6] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 6,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &outputInfo
        };

        context.Api.UpdateDescriptorSets(context.Device, 7, writes, 0, null);
        descriptorSetsByOutputViewHandle[outputImage.ViewHandle] = descriptorSet;
        Log.Debug("Compositor descriptor set created for output view {OutputViewHandle}.", outputImage.ViewHandle);
        return descriptorSet;
    }

    private DescriptorSet AllocateDescriptorSet()
    {
        unsafe
        {
            var descriptorSetLayoutLocal = descriptorSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = context.DescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &descriptorSetLayoutLocal
            };

            context.Api.AllocateDescriptorSets(context.Device, in allocInfo, out var descriptorSet).ThrowOnError();
            return descriptorSet;
        }
    }

    private void ResetDescriptorCache()
    {
        if (descriptorSetsByOutputViewHandle.Count == 0)
            return;

        context.WaitForSubmittedCommandBuffers();
        var descriptorSets = descriptorSetsByOutputViewHandle.Values.ToArray();
        fixed (DescriptorSet* pDescriptorSets = descriptorSets)
        {
            context.Api.FreeDescriptorSets(
                context.Device,
                context.DescriptorPool,
                (uint)descriptorSets.Length,
                pDescriptorSets).ThrowOnError();
        }
        descriptorSetsByOutputViewHandle.Clear();
    }

    public void Dispose()
    {
        ResetDescriptorCache();
        if (pipeline.Handle != default)
            context.Api.DestroyPipeline(context.Device, pipeline, default);
        if (pipelineLayout.Handle != default)
            context.Api.DestroyPipelineLayout(context.Device, pipelineLayout, default);
        if (descriptorSetLayout.Handle != default)
            context.Api.DestroyDescriptorSetLayout(context.Device, descriptorSetLayout, default);
    }
}
