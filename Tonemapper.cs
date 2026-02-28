using System;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

public sealed unsafe class Tonemapper : IDisposable
{
    private readonly Context context;
    private readonly DescriptorPool descriptorPool;
    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly DescriptorSet descriptorSet;
    private readonly PipelineLayout pipelineLayout;
    private readonly Pipeline pipeline;
    private ulong lastInputViewHandle;
    private ulong lastOutputViewHandle;
    private bool hasLoggedDispatch;

    public Tonemapper(Context context)
    {
        this.context = context;
        var shaderBytes = EmbeddedAssets.ReadByFileName("Tonemapper.spv");
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

        var layoutBindings = stackalloc DescriptorSetLayoutBinding[2];
        layoutBindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);

        var descriptorSetLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = layoutBindings
        };
        context.Api.CreateDescriptorSetLayout(context.Device, in descriptorSetLayoutInfo, default, out var descriptorSetLayoutLocal).ThrowOnError();

        var poolSize = new DescriptorPoolSize(DescriptorType.StorageImage, 2);
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
        Log.Information("Tonemapper pipeline created.");
    }

    public void Apply(ImageResource inputImage, ImageResource outputImage)
    {
        UpdateBindings(inputImage, outputImage);

        var commandBuffer = context.Pool.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        inputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        outputImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderWriteBit);

        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Compute, pipeline);
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Compute, pipelineLayout, 0, 1, in descriptorSet, 0, null);

        const uint groupSize = 16;
        var width = (uint)Math.Max(1, outputImage.Size.Width);
        var height = (uint)Math.Max(1, outputImage.Size.Height);
        var groupCountX = (width + groupSize - 1) / groupSize;
        var groupCountY = (height + groupSize - 1) / groupSize;
        if (!hasLoggedDispatch)
        {
            Log.Information("Tonemapper dispatch: {GroupCountX}x{GroupCountY} groups for {Width}x{Height}.", groupCountX, groupCountY, width, height);
            hasLoggedDispatch = true;
        }
        context.Api.CmdDispatch(commandBuffer.InternalHandle, groupCountX, groupCountY, 1);
        commandBuffer.Submit();
    }

    private void UpdateBindings(ImageResource inputImage, ImageResource outputImage)
    {
        if (lastInputViewHandle == inputImage.ViewHandle && lastOutputViewHandle == outputImage.ViewHandle)
            return;

        var inputInfo = new DescriptorImageInfo(default, new ImageView(inputImage.ViewHandle), ImageLayout.General);
        var outputInfo = new DescriptorImageInfo(default, new ImageView(outputImage.ViewHandle), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &inputInfo
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &outputInfo
        };

        context.Api.UpdateDescriptorSets(context.Device, 2, writes, 0, null);
        lastInputViewHandle = inputImage.ViewHandle;
        lastOutputViewHandle = outputImage.ViewHandle;
        Log.Information("Tonemapper image bindings updated.");
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
