using System;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

internal sealed unsafe class ComputeRaytracer : GpuRaytracer
{
    private readonly DescriptorPool descriptorPool;
    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly DescriptorSet descriptorSet;
    private readonly PipelineLayout pipelineLayout;
    private readonly Pipeline pipeline;

    private GpuBuffer instancesBuffer;
    private GpuBuffer meshBuffer;

    public ComputeRaytracer(Context context, Scene scene)
        : base(context, scene)
    {
        var shaderBytes = EmbeddedAssets.ReadByFileName("PathTracer.spv");
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
            Context.Api.CreateShaderModule(Context.Device, in shaderInfo, default, out computeModule).ThrowOnError();
        }

        var layoutBindings = stackalloc DescriptorSetLayoutBinding[8];
        layoutBindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[3] = new DescriptorSetLayoutBinding(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[4] = new DescriptorSetLayoutBinding(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[5] = new DescriptorSetLayoutBinding(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[6] = new DescriptorSetLayoutBinding(6, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[7] = new DescriptorSetLayoutBinding(7, DescriptorType.CombinedImageSampler, MaxTextures, ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit);

        var bindingFlags = stackalloc DescriptorBindingFlags[8];
        bindingFlags[7] = DescriptorBindingFlags.PartiallyBoundBit |
                          DescriptorBindingFlags.VariableDescriptorCountBit |
                          DescriptorBindingFlags.UpdateAfterBindBit;

        var bindingFlagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 8,
            PBindingFlags = bindingFlags
        };

        var descriptorSetLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 8,
            PBindings = layoutBindings,
            PNext = &bindingFlagsInfo
        };
        Context.Api.CreateDescriptorSetLayout(Context.Device, in descriptorSetLayoutInfo, default, out var descriptorSetLayoutLocal).ThrowOnError();

        var poolSizes = stackalloc DescriptorPoolSize[3];
        poolSizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 2);
        poolSizes[1] = new DescriptorPoolSize(DescriptorType.StorageImage, 5);
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
            Size = (uint)sizeof(PushConstantsDataGpu)
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
        Context.Api.CreateComputePipelines(Context.Device, default, 1, in pipelineInfo, default, out var pipelineLocal).ThrowOnError();
        Context.Api.DestroyShaderModule(Context.Device, computeModule, default);

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
        pipeline = pipelineLocal;
        Log.Information("Compute raytracer pipeline and descriptors created.");

        var initCommandBuffer = Context.CreateCommandBuffer();
        initCommandBuffer.BeginRecording();
        UpdateSceneResources(initCommandBuffer, force: true);
        initCommandBuffer.SubmitAndWait();
    }

    protected override void UpdateSceneResources(CommandBufferPool.PooledCommandBuffer commandBuffer, bool force)
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

        commandBuffer.RetainForExecution(previousInstancesBuffer);
        commandBuffer.RetainForExecution(previousMeshBuffer);

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
            DstBinding = 6,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &meshInfo
        };
        Context.Api.UpdateDescriptorSets(Context.Device, 2, writes, 0, null);

        Scene.ClearDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas);
        Log.Information("Compute scene resources updated: instancesBytes={InstancesBytes}, meshBytes={MeshBytes}.", instanceBytes.Length, meshBytes.Length);
    }

    protected override void ExecuteRaytracing(CommandBuffer commandBuffer, ImageResource image, PushConstantsDataGpu pushConstants)
    {
        Context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        Context.Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, pipelineLayout, 0, 1, in descriptorSet, 0, null);

        Context.Api.CmdPushConstants(
            commandBuffer,
            pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)sizeof(PushConstantsDataGpu),
            &pushConstants);

        const uint groupSize = 16;
        var width = (uint)Math.Max(1, image.Size.Width);
        var height = (uint)Math.Max(1, image.Size.Height);
        var groupCountX = (width + groupSize - 1) / groupSize;
        var groupCountY = (height + groupSize - 1) / groupSize;
        if (pushConstants.Push.Frame < 3)
            Log.Debug("Compute dispatch frame={Frame}: groups={GroupCountX}x{GroupCountY}.", pushConstants.Push.Frame, groupCountX, groupCountY);
        Context.Api.CmdDispatch(commandBuffer, groupCountX, groupCountY, 1);
    }

    protected override DescriptorSet GetDescriptorSet() => descriptorSet;
    protected override DescriptorSetLayout GetDescriptorSetLayout() => descriptorSetLayout;

    public override void Dispose()
    {
        DisposeRenderImages();
        meshBuffer.Dispose();
        instancesBuffer.Dispose();

        if (pipeline.Handle != default)
            Context.Api.DestroyPipeline(Context.Device, pipeline, default);
        if (pipelineLayout.Handle != default)
            Context.Api.DestroyPipelineLayout(Context.Device, pipelineLayout, default);
        if (descriptorSetLayout.Handle != default)
            Context.Api.DestroyDescriptorSetLayout(Context.Device, descriptorSetLayout, default);
        if (descriptorPool.Handle != default)
            Context.Api.DestroyDescriptorPool(Context.Device, descriptorPool, default);
    }
}
