using System;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Raytracing;

internal sealed unsafe class ComputeRaytracer : GpuRaytracer
{
    private readonly DescriptorPool descriptorPool;
    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly DescriptorSet descriptorSet;
    private readonly PipelineLayout pipelineLayout;
    private readonly Pipeline fullPathPipeline;
    private readonly Pipeline aoPipeline;
    private readonly Pipeline directPipeline;

    private GpuBuffer instancesBuffer;
    private GpuBuffer meshBuffer;

    public ComputeRaytracer(Context context, Scene scene)
        : base(context, scene)
    {
        var fullPathShaderBytes = EmbeddedAssets.ReadByFileName("PathTracer.spv");
        var aoShaderBytes = EmbeddedAssets.ReadByFileName("PathTracerAo.spv");
        var directShaderBytes = EmbeddedAssets.ReadByFileName("PathTracerDirect.spv");
        using var mainName = new ByteString("main");

        var fullPathModule = CreateShaderModule(fullPathShaderBytes);
        var aoModule = CreateShaderModule(aoShaderBytes);
        var directModule = CreateShaderModule(directShaderBytes);

        var layoutBindings = stackalloc DescriptorSetLayoutBinding[10];
        layoutBindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[3] = new DescriptorSetLayoutBinding(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[4] = new DescriptorSetLayoutBinding(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[5] = new DescriptorSetLayoutBinding(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[6] = new DescriptorSetLayoutBinding(6, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[7] = new DescriptorSetLayoutBinding(7, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[8] = new DescriptorSetLayoutBinding(8, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[9] = new DescriptorSetLayoutBinding(9, DescriptorType.CombinedImageSampler, MaxTextures, ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit);

        var bindingFlags = stackalloc DescriptorBindingFlags[10];
        bindingFlags[9] = DescriptorBindingFlags.PartiallyBoundBit |
                          DescriptorBindingFlags.VariableDescriptorCountBit |
                          DescriptorBindingFlags.UpdateAfterBindBit;

        var bindingFlagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 10,
            PBindingFlags = bindingFlags
        };

        var descriptorSetLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 10,
            PBindings = layoutBindings,
            PNext = &bindingFlagsInfo
        };
        Context.Api.CreateDescriptorSetLayout(Context.Device, in descriptorSetLayoutInfo, default, out var descriptorSetLayoutLocal).ThrowOnError();

        var poolSizes = stackalloc DescriptorPoolSize[3];
        poolSizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 3);
        poolSizes[1] = new DescriptorPoolSize(DescriptorType.StorageImage, 6);
        poolSizes[2] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, MaxTextures);
        var descriptorPoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 3,
            PPoolSizes = poolSizes
        };
        Context.Api.CreateDescriptorPool(Context.Device, in descriptorPoolInfo, default, out var descriptorPoolLocal).ThrowOnError();

        var descriptorCount = MaxTextures;
        var variableCountInfo = new DescriptorSetVariableDescriptorCountAllocateInfo
        {
            SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
            DescriptorSetCount = 1,
            PDescriptorCounts = &descriptorCount
        };

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPoolLocal,
            DescriptorSetCount = 1,
            PSetLayouts = &descriptorSetLayoutLocal,
            PNext = &variableCountInfo
        };
        Context.Api.AllocateDescriptorSets(Context.Device, in allocInfo, out var descriptorSetLocal).ThrowOnError();

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)sizeof(PushDataGpu)
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayoutLocal,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };
        Context.Api.CreatePipelineLayout(Context.Device, in pipelineLayoutInfo, default, out var pipelineLayoutLocal).ThrowOnError();

        var fullPathPipelineLocal = CreateComputePipeline(fullPathModule, pipelineLayoutLocal, mainName);
        var aoPipelineLocal = CreateComputePipeline(aoModule, pipelineLayoutLocal, mainName);
        var directPipelineLocal = CreateComputePipeline(directModule, pipelineLayoutLocal, mainName);

        Context.Api.DestroyShaderModule(Context.Device, directModule, default);
        Context.Api.DestroyShaderModule(Context.Device, aoModule, default);
        Context.Api.DestroyShaderModule(Context.Device, fullPathModule, default);

        instancesBuffer = new GpuBuffer(
            Context,
            16,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            new byte[16]);
        meshBuffer = new GpuBuffer(
            Context,
            16,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            new byte[16]);

        descriptorSetLayout = descriptorSetLayoutLocal;
        descriptorPool = descriptorPoolLocal;
        descriptorSet = descriptorSetLocal;
        pipelineLayout = pipelineLayoutLocal;
        fullPathPipeline = fullPathPipelineLocal;
        aoPipeline = aoPipelineLocal;
        directPipeline = directPipelineLocal;
        Log.Information("Compute raytracer pipelines and descriptors created.");

        var initCommandBuffer = Context.CreateCommandBuffer();
        Context.BeginCommandBuffer(initCommandBuffer);
        UpdateSceneResources(initCommandBuffer, force: true);
        Context.SubmitAndWait(initCommandBuffer);
    }

    protected override void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force)
    {
        if (!force && !Scene.IsDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas))
            return;

        var instanceBytes = Scene.BuildComputeInstanceData();
        var meshBytes = Scene.BuildMeshAddressData();

        var previousInstancesBuffer = instancesBuffer;
        var previousMeshBuffer = meshBuffer;

        instancesBuffer = new GpuBuffer(
            Context,
            (ulong)instanceBytes.Length,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            instanceBytes);
        meshBuffer = new GpuBuffer(
            Context,
            (ulong)meshBytes.Length,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            meshBytes);

        Context.RetainForExecution(commandBuffer, previousInstancesBuffer);
        Context.RetainForExecution(commandBuffer, previousMeshBuffer);

        var instancesInfo = new DescriptorBufferInfo(instancesBuffer.Handle, 0, instancesBuffer.Size);
        var meshInfo = new DescriptorBufferInfo(meshBuffer.Handle, 0, meshBuffer.Size);

        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &instancesInfo
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 7,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &meshInfo
        };
        Context.Api.UpdateDescriptorSets(Context.Device, 2, writes, 0, null);

        Scene.ClearDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas);
        Log.Information("Compute scene resources updated: instancesBytes={InstancesBytes}, meshBytes={MeshBytes}.", instanceBytes.Length, meshBytes.Length);
    }

    protected override void ExecuteRaytracing(CommandBuffer commandBuffer, ImageResource image, PushDataGpu pushConstants)
    {
        var selectedPipeline = Scene.RenderMode switch
        {
            RenderMode.AmbientOcclusion => aoPipeline,
            RenderMode.DirectLighting => directPipeline,
            RenderMode.PathTracing => fullPathPipeline,
            _ => fullPathPipeline
        };
        Context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, selectedPipeline);
        Context.Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, pipelineLayout, 0, 1, in descriptorSet, 0, null);

        Context.Api.CmdPushConstants(
            commandBuffer,
            pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)sizeof(PushDataGpu),
            &pushConstants);

        const uint groupSize = 16;
        var width = (uint)Math.Max(1, image.Size.Width);
        var height = (uint)Math.Max(1, image.Size.Height);
        var groupCountX = (width + groupSize - 1) / groupSize;
        var groupCountY = (height + groupSize - 1) / groupSize;
        if (pushConstants.Frame < 3)
            Log.Debug("Compute dispatch frame={Frame}: groups={GroupCountX}x{GroupCountY}.", pushConstants.Frame, groupCountX, groupCountY);
        Context.Api.CmdDispatch(commandBuffer, groupCountX, groupCountY, 1);
    }

    protected override DescriptorSet GetDescriptorSet() => descriptorSet;
    protected override DescriptorSetLayout GetDescriptorSetLayout() => descriptorSetLayout;

    private ShaderModule CreateShaderModule(ReadOnlySpan<byte> shaderBytes)
    {
        fixed (byte* pShader = shaderBytes)
        {
            var shaderInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)shaderBytes.Length,
                PCode = (uint*)pShader
            };
            Context.Api.CreateShaderModule(Context.Device, in shaderInfo, default, out var module).ThrowOnError();
            return module;
        }
    }

    private Pipeline CreateComputePipeline(ShaderModule shaderModule, PipelineLayout layout, ByteString mainName)
    {
        var stageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = shaderModule,
            PName = mainName
        };
        var pipelineInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stageInfo,
            Layout = layout
        };
        Context.Api.CreateComputePipelines(Context.Device, default, 1, in pipelineInfo, default, out var pipeline).ThrowOnError();
        return pipeline;
    }

    public override void Dispose()
    {
        DisposeCommonResources();
        meshBuffer.Dispose();
        instancesBuffer.Dispose();

        if (aoPipeline.Handle != default)
            Context.Api.DestroyPipeline(Context.Device, aoPipeline, default);
        if (directPipeline.Handle != default)
            Context.Api.DestroyPipeline(Context.Device, directPipeline, default);
        if (fullPathPipeline.Handle != default)
            Context.Api.DestroyPipeline(Context.Device, fullPathPipeline, default);
        if (pipelineLayout.Handle != default)
            Context.Api.DestroyPipelineLayout(Context.Device, pipelineLayout, default);
        if (descriptorSetLayout.Handle != default)
            Context.Api.DestroyDescriptorSetLayout(Context.Device, descriptorSetLayout, default);
        if (descriptorPool.Handle != default)
            Context.Api.DestroyDescriptorPool(Context.Device, descriptorPool, default);
    }
}
