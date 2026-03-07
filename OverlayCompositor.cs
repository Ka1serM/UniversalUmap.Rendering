using System;
using System.Runtime.InteropServices;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

public sealed unsafe class OverlayCompositor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayPushConstants
    {
        public uint SelectedInstanceId;
    }

    private readonly Context context;
    private readonly DescriptorPool descriptorPool;
    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly DescriptorSet descriptorSet;
    private readonly PipelineLayout pipelineLayout;
    private readonly Pipeline pipeline;
    private ulong lastColorInputViewHandle;
    private ulong lastCryptoInputViewHandle;
    private ulong lastPositionInputViewHandle;
    private ulong lastOutputViewHandle;
    private bool hasLoggedDispatch;

    public OverlayCompositor(Context context)
    {
        this.context = context;
        var shaderBytes = EmbeddedAssets.ReadByFileName("OverlayComposite.spv");
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

        var layoutBindings = stackalloc DescriptorSetLayoutBinding[4];
        layoutBindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[3] = new DescriptorSetLayoutBinding(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);

        var descriptorSetLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = layoutBindings
        };
        context.Api.CreateDescriptorSetLayout(context.Device, in descriptorSetLayoutInfo, default, out var descriptorSetLayoutLocal).ThrowOnError();

        var poolSize = new DescriptorPoolSize(DescriptorType.StorageImage, 4);
        var descriptorPoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize
        };
        context.Api.CreateDescriptorPool(context.Device, in descriptorPoolInfo, default, out var descriptorPoolLocal).ThrowOnError();

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPoolLocal,
            DescriptorSetCount = 1,
            PSetLayouts = &descriptorSetLayoutLocal
        };
        context.Api.AllocateDescriptorSets(context.Device, in allocInfo, out var descriptorSetLocal).ThrowOnError();

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
            Size = (uint)sizeof(OverlayPushConstants)
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
        descriptorPool = descriptorPoolLocal;
        descriptorSet = descriptorSetLocal;
        pipelineLayout = pipelineLayoutLocal;
        pipeline = pipelineLocal;
        Log.Information("OverlayCompositor pipeline created.");
    }

    public void Record(
        Context.CommandBuffer commandBuffer,
        ImageResource colorInputImage,
        ImageResource cryptoInputImage,
        ImageResource positionInputImage,
        uint selectedInstanceId,
        ImageResource outputImage)
    {
        UpdateBindings(colorInputImage, cryptoInputImage, positionInputImage, outputImage);

        colorInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        cryptoInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        positionInputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        outputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderWriteBit);

        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Compute, pipeline);
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Compute, pipelineLayout, 0, 1, in descriptorSet, 0, null);
        var pushConstants = new OverlayPushConstants { SelectedInstanceId = selectedInstanceId };
        context.Api.CmdPushConstants(
            commandBuffer.InternalHandle,
            pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)sizeof(OverlayPushConstants),
            &pushConstants);

        const uint groupSize = 16;
        var width = (uint)Math.Max(1, outputImage.Size.Width);
        var height = (uint)Math.Max(1, outputImage.Size.Height);
        var groupCountX = (width + groupSize - 1) / groupSize;
        var groupCountY = (height + groupSize - 1) / groupSize;
        if (!hasLoggedDispatch)
        {
            Log.Information("OverlayCompositor dispatch: {GroupCountX}x{GroupCountY} groups for {Width}x{Height}.", groupCountX, groupCountY, width, height);
            hasLoggedDispatch = true;
        }
        context.Api.CmdDispatch(commandBuffer.InternalHandle, groupCountX, groupCountY, 1);
    }

    private void UpdateBindings(
        ImageResource colorInputImage,
        ImageResource cryptoInputImage,
        ImageResource positionInputImage,
        ImageResource outputImage)
    {
        if (lastColorInputViewHandle == colorInputImage.ViewHandle &&
            lastCryptoInputViewHandle == cryptoInputImage.ViewHandle &&
            lastPositionInputViewHandle == positionInputImage.ViewHandle &&
            lastOutputViewHandle == outputImage.ViewHandle)
            return;

        var colorInputInfo = new DescriptorImageInfo(default, new ImageView(colorInputImage.ViewHandle), ImageLayout.General);
        var cryptoInputInfo = new DescriptorImageInfo(default, new ImageView(cryptoInputImage.ViewHandle), ImageLayout.General);
        var positionInputInfo = new DescriptorImageInfo(default, new ImageView(positionInputImage.ViewHandle), ImageLayout.General);
        var outputInfo = new DescriptorImageInfo(default, new ImageView(outputImage.ViewHandle), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[4];
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
            PImageInfo = &cryptoInputInfo
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &positionInputInfo
        };
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 3,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &outputInfo
        };

        context.Api.UpdateDescriptorSets(context.Device, 4, writes, 0, null);
        lastColorInputViewHandle = colorInputImage.ViewHandle;
        lastCryptoInputViewHandle = cryptoInputImage.ViewHandle;
        lastPositionInputViewHandle = positionInputImage.ViewHandle;
        lastOutputViewHandle = outputImage.ViewHandle;
        Log.Information("OverlayCompositor image bindings updated (color/crypto/position/output).");
    }

    public void Dispose()
    {
        if (pipeline.Handle != default)
            context.Api.DestroyPipeline(context.Device, pipeline, default);
        if (pipelineLayout.Handle != default)
            context.Api.DestroyPipelineLayout(context.Device, pipelineLayout, default);
        if (descriptorSetLayout.Handle != default)
            context.Api.DestroyDescriptorSetLayout(context.Device, descriptorSetLayout, default);
        if (descriptorPool.Handle != default)
            context.Api.DestroyDescriptorPool(context.Device, descriptorPool, default);
    }
}
