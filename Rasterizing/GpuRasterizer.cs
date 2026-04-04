using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Rasterizing;

internal sealed unsafe class GpuRasterizer : IGpuRenderPath
{
    public int ShaderPixelSizePercent { get; set; } = 100;
    [StructLayout(LayoutKind.Sequential)]
    private struct CullPushConstants
    {
        public int HasHistory;
        public int IsMoving;
        public int Pad0;
        public int Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RasterPushConstants
    {
        public uint MeshId;
        public uint VisibleOffset;
        public uint Pad0;
        public uint Pad1;
    }

    private const uint MaxTextures = 10_000;
    private static readonly Format DepthFormat = Format.D32Sfloat;

    private readonly Context context;
    private readonly Scene scene;
    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly DescriptorSet descriptorSet;
    private readonly PipelineLayout computePipelineLayout;
    private readonly PipelineLayout graphicsPipelineLayout;
    private readonly Pipeline cullResetPipeline;
    private readonly Pipeline cullInstancesPipeline;
    private readonly Pipeline buildIndirectPipeline;
    private readonly Pipeline shadowOverlayPipeline;
    private readonly Pipeline graphicsPipeline;
    private readonly Sampler previousPositionSampler;

    private VulkanBuffer instancesBuffer;
    private VulkanBuffer meshBuffer;
    private VulkanBuffer meshRasterMetadataBuffer;
    private VulkanBuffer visibleInstanceIdsBuffer;
    private VulkanBuffer visibleCountsBuffer;
    private VulkanBuffer indirectCommandsBuffer;
    private VulkanBuffer sceneSettingsBuffer;

    private VulkanImage? outputColorImage;
    private VulkanImage? albedoImage;
    private VulkanImage? normalImage;
    private VulkanImage? cryptoImage;
    private VulkanImage? positionImage;
    private VulkanImage? adaptiveStateImage;
    private VulkanDepthImage? depthImage;
    private PixelSize renderImageSize;
    private ulong lastBoundColorImageViewHandle;
    private bool settingsDirty = true;
    private bool hasHistory;
    private uint meshCount;

    public GpuRasterizer(Context context, Scene scene)
    {
        this.context = context;
        this.scene = scene;

        var layoutBindings = stackalloc DescriptorSetLayoutBinding[15];
        layoutBindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit);
        layoutBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[3] = new DescriptorSetLayoutBinding(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[4] = new DescriptorSetLayoutBinding(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[5] = new DescriptorSetLayoutBinding(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[6] = new DescriptorSetLayoutBinding(6, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[7] = new DescriptorSetLayoutBinding(7, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit);
        layoutBindings[8] = new DescriptorSetLayoutBinding(8, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit);
        layoutBindings[9] = new DescriptorSetLayoutBinding(9, DescriptorType.CombinedImageSampler, MaxTextures, ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit);
        layoutBindings[10] = new DescriptorSetLayoutBinding(10, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit);
        layoutBindings[11] = new DescriptorSetLayoutBinding(11, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit);
        layoutBindings[12] = new DescriptorSetLayoutBinding(12, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[13] = new DescriptorSetLayoutBinding(13, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        layoutBindings[14] = new DescriptorSetLayoutBinding(14, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit);

        var bindingFlags = stackalloc DescriptorBindingFlags[15];
        bindingFlags[9] = DescriptorBindingFlags.PartiallyBoundBit |
                          DescriptorBindingFlags.VariableDescriptorCountBit |
                          DescriptorBindingFlags.UpdateAfterBindBit;

        var bindingFlagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 15,
            PBindingFlags = bindingFlags
        };

        var descriptorSetLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 15,
            PBindings = layoutBindings,
            PNext = &bindingFlagsInfo
        };
        context.Api.CreateDescriptorSetLayout(context.Device, in descriptorSetLayoutInfo, default, out descriptorSetLayout).ThrowOnError();

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
            DescriptorPool = context.DescriptorPool,
            DescriptorSetCount = 1,
            PNext = &variableCountInfo
        };
        var descriptorSetLayoutLocal = descriptorSetLayout;
        allocInfo.PSetLayouts = &descriptorSetLayoutLocal;
        context.Api.AllocateDescriptorSets(context.Device, in allocInfo, out descriptorSet).ThrowOnError();

        CreateComputeLayoutAndPipelines(out computePipelineLayout, out cullResetPipeline, out cullInstancesPipeline, out buildIndirectPipeline, out shadowOverlayPipeline);
        CreateGraphicsPipeline(out graphicsPipelineLayout, out graphicsPipeline);
        previousPositionSampler = CreatePreviousPositionSampler();

        instancesBuffer = CreatePlaceholderBuffer(BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshBuffer = CreatePlaceholderBuffer(BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshRasterMetadataBuffer = CreatePlaceholderBuffer(BufferUsageFlags.StorageBufferBit);
        visibleInstanceIdsBuffer = CreatePlaceholderBuffer(BufferUsageFlags.StorageBufferBit);
        visibleCountsBuffer = CreatePlaceholderBuffer(BufferUsageFlags.StorageBufferBit);
        indirectCommandsBuffer = CreatePlaceholderBuffer(BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit);
        sceneSettingsBuffer = new VulkanBuffer(
            context,
            (ulong)Marshal.SizeOf<SceneSettingsDataGpu>(),
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        var initCommandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(initCommandBuffer);
        UpdateSceneResources(initCommandBuffer, force: true);
        UpdateDescriptorBuffers();
        context.SubmitAndWait(initCommandBuffer);

        scene.RenderSettings.PropertyChanged += (_, _) => settingsDirty = true;
        scene.Camera.Changed += () => settingsDirty = true;
        scene.Environment.PropertyChanged += (_, _) => settingsDirty = true;
    }

    public VulkanImage OutputColor => outputColorImage ?? throw new InvalidOperationException("Raster output image is not initialized.");
    public VulkanImage OutputAlbedo => albedoImage ?? throw new InvalidOperationException("Raster albedo image is not initialized.");
    public VulkanImage OutputNormal => normalImage ?? throw new InvalidOperationException("Raster normal image is not initialized.");
    public VulkanImage OutputCrypto => cryptoImage ?? throw new InvalidOperationException("Raster crypto image is not initialized.");
    public VulkanImage OutputPosition => positionImage ?? throw new InvalidOperationException("Raster position image is not initialized.");
    public VulkanImage OutputAdaptiveState => adaptiveStateImage ?? throw new InvalidOperationException("Raster adaptive image is not initialized.");
    public PixelSize RenderImageSize => renderImageSize;
    public bool PickBuffersFlippedY => false;

    public void Record(PixelSize renderSize, VulkanImage image, Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData)
    {
        EnsureRenderImages(renderSize, commandBuffer);
        UpdateSceneResources(commandBuffer, force: false);
        UpdateDescriptorBuffers();
        UpdateOutputImageBindings();
        UpdateSceneSettingsBuffer(renderData);
        UpdateTextureBindings();

        RunCullPass(commandBuffer, renderData);
        RunRasterPass(commandBuffer);

        if (renderData.RenderSettings.RenderMode == (int)RenderMode.RasterizedWithRayTracedShadows)
            RunShadowOverlay(commandBuffer, renderSize);

        hasHistory = true;
        scene.ClearDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
    }

    public bool QueryPixelUInt(VulkanImage image, int pixelX, int pixelY, out uint value)
    {
        value = 0;
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
        if (image.CurrentLayout == (uint)ImageLayout.Undefined)
            return false;

        var maxX = renderImageSize.Width - 1;
        var maxY = renderImageSize.Height - 1;
        if (pixelX < 0 || pixelY < 0 || pixelX > maxX || pixelY > maxY)
            return false;

        value = ReadPixelHalf4(image, pixelX, pixelY);
        return true;
    }

    private void CreateComputeLayoutAndPipelines(out PipelineLayout layout, out Pipeline reset, out Pipeline cull, out Pipeline build, out Pipeline shadow)
    {
        var cullPushRange = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Offset = 0, Size = (uint)sizeof(CullPushConstants) };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &cullPushRange
        };
        var descriptorSetLayoutLocal = descriptorSetLayout;
        layoutInfo.PSetLayouts = &descriptorSetLayoutLocal;
        context.Api.CreatePipelineLayout(context.Device, in layoutInfo, default, out layout).ThrowOnError();

        reset = CreateComputePipeline("Assets/Shaders/Raster/CullReset.spv", layout);
        cull = CreateComputePipeline("Assets/Shaders/Raster/CullInstances.spv", layout);
        build = CreateComputePipeline("Assets/Shaders/Raster/BuildIndirect.spv", layout);
        shadow = CreateComputePipeline("Assets/Shaders/Raster/RtShadowOverlay.spv", layout);
    }

    private void CreateGraphicsPipeline(out PipelineLayout layout, out Pipeline pipeline)
    {
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(RasterPushConstants)
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange
        };
        var descriptorSetLayoutLocal = descriptorSetLayout;
        layoutInfo.PSetLayouts = &descriptorSetLayoutLocal;
        context.Api.CreatePipelineLayout(context.Device, in layoutInfo, default, out layout).ThrowOnError();

        var vertModule = CreateShaderModule("Assets/Shaders/Raster/DrawVS.spv");
        var fragModule = CreateShaderModule("Assets/Shaders/Raster/DrawFS.spv");
        using var mainName = new ByteString("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertModule, PName = mainName };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = mainName };

        var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.BackBit,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f
        };
        var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = true,
            DepthWriteEnable = true,
            DepthCompareOp = CompareOp.LessOrEqual
        };

        var colorAttachments = stackalloc PipelineColorBlendAttachmentState[6];
        for (var i = 0; i < 6; i++)
        {
            colorAttachments[i] = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };
        }

        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 6,
            PAttachments = colorAttachments
        };

        var dynamicStates = stackalloc DynamicState[2];
        dynamicStates[0] = DynamicState.Viewport;
        dynamicStates[1] = DynamicState.Scissor;
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates
        };

        var colorFormats = stackalloc Format[6];
        colorFormats[0] = Format.R32G32B32A32Sfloat;
        colorFormats[1] = Format.R8G8B8A8Unorm;
        colorFormats[2] = Format.R16G16B16A16Sfloat;
        colorFormats[3] = Format.R32Uint;
        colorFormats[4] = Format.R16G16B16A16Sfloat;
        colorFormats[5] = Format.R32G32B32A32Sfloat;

        var renderingInfo = new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount = 6,
            PColorAttachmentFormats = colorFormats,
            DepthAttachmentFormat = DepthFormat
        };

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisample,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlend,
            PDynamicState = &dynamicState,
            Layout = layout,
            PNext = &renderingInfo
        };
        context.Api.CreateGraphicsPipelines(context.Device, default, 1, in pipelineInfo, default, out pipeline).ThrowOnError();

        context.Api.DestroyShaderModule(context.Device, vertModule, default);
        context.Api.DestroyShaderModule(context.Device, fragModule, default);
    }

    private Sampler CreatePreviousPositionSampler()
    {
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f
        };

        context.Api.CreateSampler(context.Device, in samplerInfo, default, out var sampler).ThrowOnError();
        return sampler;
    }

    private VulkanBuffer CreatePlaceholderBuffer(BufferUsageFlags usage)
    {
        return new VulkanBuffer(
            context,
            16,
            usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            new byte[16]);
    }

    private void EnsureRenderImages(PixelSize size, Context.CommandBuffer commandBuffer)
    {
        if (outputColorImage is not null && renderImageSize == size)
            return;

        if (outputColorImage is not null) context.RetainForExecution(commandBuffer, outputColorImage);
        if (albedoImage is not null) context.RetainForExecution(commandBuffer, albedoImage);
        if (normalImage is not null) context.RetainForExecution(commandBuffer, normalImage);
        if (cryptoImage is not null) context.RetainForExecution(commandBuffer, cryptoImage);
        if (positionImage is not null) context.RetainForExecution(commandBuffer, positionImage);
        if (adaptiveStateImage is not null) context.RetainForExecution(commandBuffer, adaptiveStateImage);
        if (depthImage is not null) context.RetainForExecution(commandBuffer, depthImage);

        var supportedHandles = new string[0];
        outputColorImage = new VulkanImage(context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        albedoImage = new VulkanImage(context, (uint)Format.R8G8B8A8Unorm, size, false, supportedHandles);
        normalImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        cryptoImage = new VulkanImage(context, (uint)Format.R32Uint, size, false, supportedHandles);
        positionImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        adaptiveStateImage = new VulkanImage(context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        depthImage = new VulkanDepthImage(context, DepthFormat, size);
        renderImageSize = size;
        lastBoundColorImageViewHandle = 0;
        hasHistory = false;
    }

    private void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force)
    {
        if (!force && !scene.IsDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas))
            return;

        var instanceBytes = scene.BuildInstanceData();
        var meshBytes = scene.BuildMeshAddressData();
        var rasterMetadataBytes = scene.BuildMeshRasterMetadata();

        var previousInstances = instancesBuffer;
        var previousMeshes = meshBuffer;
        var previousRasterMetadata = meshRasterMetadataBuffer;
        var previousVisibleInstanceIds = visibleInstanceIdsBuffer;
        var previousVisibleCounts = visibleCountsBuffer;
        var previousIndirectCommands = indirectCommandsBuffer;

        instancesBuffer = new VulkanBuffer(context, (ulong)instanceBytes.Length, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, instanceBytes);
        meshBuffer = new VulkanBuffer(context, (ulong)meshBytes.Length, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, meshBytes);
        meshRasterMetadataBuffer = new VulkanBuffer(context, (ulong)rasterMetadataBytes.Length, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, rasterMetadataBytes);

        var instanceCount = (uint)Math.Max(1, instanceBytes.Length / StructPacking.SizeOf<InstanceGpu>());
        meshCount = (uint)Math.Max(1, rasterMetadataBytes.Length / StructPacking.SizeOf<MeshRasterMetadataGpu>());

        visibleInstanceIdsBuffer = new VulkanBuffer(context, (ulong)(Math.Max(1u, instanceCount) * sizeof(uint)), BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        visibleCountsBuffer = new VulkanBuffer(context, (ulong)(Math.Max(1u, meshCount) * sizeof(uint)), BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        indirectCommandsBuffer = new VulkanBuffer(context, (ulong)(Math.Max(1u, meshCount) * StructPacking.SizeOf<DrawIndirectCommandGpu>()), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        context.RetainForExecution(commandBuffer, previousInstances);
        context.RetainForExecution(commandBuffer, previousMeshes);
        context.RetainForExecution(commandBuffer, previousRasterMetadata);
        context.RetainForExecution(commandBuffer, previousVisibleInstanceIds);
        context.RetainForExecution(commandBuffer, previousVisibleCounts);
        context.RetainForExecution(commandBuffer, previousIndirectCommands);

        scene.ClearDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas);
        hasHistory = false;
    }

    private void UpdateDescriptorBuffers()
    {
        var instancesInfo = new DescriptorBufferInfo(instancesBuffer.Handle, 0, instancesBuffer.Size);
        var meshInfo = new DescriptorBufferInfo(meshBuffer.Handle, 0, meshBuffer.Size);
        var sceneSettingsInfo = new DescriptorBufferInfo(sceneSettingsBuffer.Handle, 0, sceneSettingsBuffer.Size);
        var rasterMeshesInfo = new DescriptorBufferInfo(meshRasterMetadataBuffer.Handle, 0, meshRasterMetadataBuffer.Size);
        var visibleIdsInfo = new DescriptorBufferInfo(visibleInstanceIdsBuffer.Handle, 0, visibleInstanceIdsBuffer.Size);
        var visibleCountsInfo = new DescriptorBufferInfo(visibleCountsBuffer.Handle, 0, visibleCountsBuffer.Size);
        var indirectInfo = new DescriptorBufferInfo(indirectCommandsBuffer.Handle, 0, indirectCommandsBuffer.Size);

        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &instancesInfo };
        writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 7, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &meshInfo };
        writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 8, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &sceneSettingsInfo };
        writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 10, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &rasterMeshesInfo };
        writes[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 11, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &visibleIdsInfo };
        writes[5] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 12, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &visibleCountsInfo };
        writes[6] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 13, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &indirectInfo };
        context.Api.UpdateDescriptorSets(context.Device, 7, writes, 0, null);
    }

    private void UpdateOutputImageBindings()
    {
        if (outputColorImage is null || lastBoundColorImageViewHandle == outputColorImage.ViewHandle)
            return;

        var colorInfo = new DescriptorImageInfo(default, new ImageView(outputColorImage.ViewHandle), ImageLayout.General);
        var albedoInfo = new DescriptorImageInfo(default, new ImageView(albedoImage!.ViewHandle), ImageLayout.General);
        var normalInfo = new DescriptorImageInfo(default, new ImageView(normalImage!.ViewHandle), ImageLayout.General);
        var cryptoInfo = new DescriptorImageInfo(default, new ImageView(cryptoImage!.ViewHandle), ImageLayout.General);
        var positionInfo = new DescriptorImageInfo(default, new ImageView(positionImage!.ViewHandle), ImageLayout.General);
        var adaptiveInfo = new DescriptorImageInfo(default, new ImageView(adaptiveStateImage!.ViewHandle), ImageLayout.General);
        var previousPositionInfo = new DescriptorImageInfo(previousPositionSampler, new ImageView(positionImage.ViewHandle), ImageLayout.ShaderReadOnlyOptimal);

        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &colorInfo };
        writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &albedoInfo };
        writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &normalInfo };
        writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &cryptoInfo };
        writes[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &positionInfo };
        writes[5] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &adaptiveInfo };
        writes[6] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = 14, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &previousPositionInfo };
        context.Api.UpdateDescriptorSets(context.Device, 7, writes, 0, null);

        lastBoundColorImageViewHandle = outputColorImage.ViewHandle;
    }

    private void UpdateSceneSettingsBuffer(Scene.RenderDataGpu renderData)
    {
        if (!settingsDirty)
            return;

        var settings = new SceneSettingsDataGpu
        {
            RenderSettings = renderData.RenderSettings,
            Environment = renderData.Environment,
            RasterCamera = scene.CaptureRasterCameraData()
        };
        sceneSettingsBuffer.Upload(StructPacking.ToBytes(new[] { settings }));
        settingsDirty = false;
    }

    private void UpdateTextureBindings()
    {
        if (!scene.IsDirty(SceneDirtyFlags.Textures))
            return;

        var textures = scene.GetTexturesSnapshot();
        if (textures.Length == 0)
        {
            scene.ClearDirty(SceneDirtyFlags.Textures);
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
                DstSet = descriptorSet,
                DstBinding = 9,
                DescriptorCount = (uint)descriptors.Length,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = pDescriptors
            };
            context.Api.UpdateDescriptorSets(context.Device, 1, in write, 0, null);
        }

        scene.ClearDirty(SceneDirtyFlags.Textures);
    }

    private void RunCullPass(Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData)
    {
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Compute, computePipelineLayout, 0, 1, in descriptorSet, 0, null);

        var groupCountMeshes = (meshCount + ShaderDefines.GROUP_SIZE - 1u) / ShaderDefines.GROUP_SIZE;
        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Compute, cullResetPipeline);
        context.Api.CmdDispatch(commandBuffer.InternalHandle, Math.Max(1u, groupCountMeshes), 1, 1);
        InsertMemoryBarrier(commandBuffer.InternalHandle, AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        var cullPush = new CullPushConstants { HasHistory = hasHistory ? 1 : 0, IsMoving = renderData.IsMoving };
        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Compute, cullInstancesPipeline);
        context.Api.CmdPushConstants(commandBuffer.InternalHandle, computePipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(CullPushConstants), &cullPush);
        var instanceCount = (uint)Math.Max(1, instancesBuffer.Size / (ulong)StructPacking.SizeOf<InstanceGpu>());
        var groupCountInstances = (instanceCount + ShaderDefines.GROUP_SIZE - 1u) / ShaderDefines.GROUP_SIZE;
        context.Api.CmdDispatch(commandBuffer.InternalHandle, Math.Max(1u, groupCountInstances), 1, 1);
        InsertMemoryBarrier(commandBuffer.InternalHandle, AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Compute, buildIndirectPipeline);
        context.Api.CmdDispatch(commandBuffer.InternalHandle, Math.Max(1u, groupCountMeshes), 1, 1);
        InsertMemoryBarrier(commandBuffer.InternalHandle, AccessFlags.ShaderWriteBit, AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderReadBit);
    }

    private void RunRasterPass(Context.CommandBuffer commandBuffer)
    {
        outputColorImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        albedoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        normalImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        cryptoImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        positionImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        adaptiveStateImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        depthImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.DepthAttachmentOptimal, AccessFlags.DepthStencilAttachmentWriteBit);

        var colorAttachments = stackalloc RenderingAttachmentInfo[6];
        colorAttachments[0] = CreateColorAttachment(outputColorImage, new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) });
        colorAttachments[1] = CreateColorAttachment(albedoImage, new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) });
        colorAttachments[2] = CreateColorAttachment(normalImage, new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) });
        colorAttachments[3] = CreateColorAttachment(cryptoImage, new ClearValue { Color = new ClearColorValue(ShaderDefines.INVALID_INSTANCE, 0f, 0f, 0f) });
        colorAttachments[4] = CreateColorAttachment(positionImage, new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) });
        colorAttachments[5] = CreateColorAttachment(adaptiveStateImage, new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) });

        var depthClear = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depthImage.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = depthClear
        };

        var renderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Math.Max(1, renderImageSize.Width), (uint)Math.Max(1, renderImageSize.Height)));
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = renderArea,
            LayerCount = 1,
            ColorAttachmentCount = 6,
            PColorAttachments = colorAttachments,
            PDepthAttachment = &depthAttachment
        };

        context.Api.CmdBeginRendering(commandBuffer.InternalHandle, in renderingInfo);

        var viewport = new Viewport(0, 0, renderImageSize.Width, renderImageSize.Height, 0f, 1f);
        context.Api.CmdSetViewport(commandBuffer.InternalHandle, 0, 1, in viewport);
        context.Api.CmdSetScissor(commandBuffer.InternalHandle, 0, 1, in renderArea);
        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, graphicsPipeline);
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, graphicsPipelineLayout, 0, 1, in descriptorSet, 0, null);

        for (uint meshId = 0; meshId < meshCount; meshId++)
        {
            var metadata = ReadMeshRasterMetadata(meshId);
            var push = new RasterPushConstants { MeshId = meshId, VisibleOffset = metadata.VisibleInstanceOffset };
            context.Api.CmdPushConstants(commandBuffer.InternalHandle, graphicsPipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(RasterPushConstants), &push);
            context.Api.CmdDrawIndirect(commandBuffer.InternalHandle, indirectCommandsBuffer.Handle, meshId * (ulong)StructPacking.SizeOf<DrawIndirectCommandGpu>(), 1, (uint)StructPacking.SizeOf<DrawIndirectCommandGpu>());
        }

        context.Api.CmdEndRendering(commandBuffer.InternalHandle);

        outputColorImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        albedoImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        normalImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        cryptoImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
        positionImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        adaptiveStateImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    private void RunShadowOverlay(Context.CommandBuffer commandBuffer, PixelSize renderSize)
    {
        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Compute, shadowOverlayPipeline);
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Compute, computePipelineLayout, 0, 1, in descriptorSet, 0, null);
        var groupCountX = ((uint)Math.Max(1, renderSize.Width) + ShaderDefines.GROUP_SIZE - 1u) / ShaderDefines.GROUP_SIZE;
        var groupCountY = ((uint)Math.Max(1, renderSize.Height) + ShaderDefines.GROUP_SIZE - 1u) / ShaderDefines.GROUP_SIZE;
        context.Api.CmdDispatch(commandBuffer.InternalHandle, groupCountX, groupCountY, 1);
    }

    private RenderingAttachmentInfo CreateColorAttachment(VulkanImage image, ClearValue clearValue)
    {
        return new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = new ImageView(image.ViewHandle),
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = clearValue
        };
    }

    private ShaderModule CreateShaderModule(string assetName)
    {
        var bytes = EmbeddedAssets.ReadByFileName(assetName);
        fixed (byte* pShader = bytes)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)bytes.Length,
                PCode = (uint*)pShader
            };
            context.Api.CreateShaderModule(context.Device, in info, default, out var module).ThrowOnError();
            return module;
        }
    }

    private Pipeline CreateComputePipeline(string assetName, PipelineLayout layout)
    {
        var module = CreateShaderModule(assetName);
        using var mainName = new ByteString("main");
        var stageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = module,
            PName = mainName
        };
        var pipelineInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stageInfo,
            Layout = layout
        };
        context.Api.CreateComputePipelines(context.Device, default, 1, in pipelineInfo, default, out var pipeline).ThrowOnError();
        context.Api.DestroyShaderModule(context.Device, module, default);
        return pipeline;
    }

    private void InsertMemoryBarrier(CommandBuffer commandBuffer, AccessFlags srcAccessMask, AccessFlags dstAccessMask)
    {
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = srcAccessMask,
            DstAccessMask = dstAccessMask
        };
        context.Api.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.AllCommandsBit,
            0,
            1,
            in barrier,
            0,
            null,
            0,
            null);
    }

    private MeshRasterMetadataGpu ReadMeshRasterMetadata(uint meshId)
    {
        unsafe
        {
            void* mapped = null;
            context.Api.MapMemory(context.Device, meshRasterMetadataBuffer.Memory, meshId * (ulong)StructPacking.SizeOf<MeshRasterMetadataGpu>(), (ulong)StructPacking.SizeOf<MeshRasterMetadataGpu>(), 0, &mapped).ThrowOnError();
            try
            {
                return *(MeshRasterMetadataGpu*)mapped;
            }
            finally
            {
                context.Api.UnmapMemory(context.Device, meshRasterMetadataBuffer.Memory);
            }
        }
    }

    private uint ReadPixelUInt(VulkanImage image, int pixelX, int pixelY)
    {
        using var staging = new VulkanBuffer(context, sizeof(uint), BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        CopyImagePixelToBuffer(image, staging, pixelX, pixelY);

        unsafe
        {
            void* mapped = null;
            context.Api.MapMemory(context.Device, staging.Memory, 0, staging.Size, 0, &mapped).ThrowOnError();
            try
            {
                return *(uint*)mapped;
            }
            finally
            {
                context.Api.UnmapMemory(context.Device, staging.Memory);
            }
        }
    }

    private Vector4 ReadPixelHalf4(VulkanImage image, int pixelX, int pixelY)
    {
        using var staging = new VulkanBuffer(context, sizeof(ushort) * 4, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        CopyImagePixelToBuffer(image, staging, pixelX, pixelY);

        unsafe
        {
            void* mapped = null;
            context.Api.MapMemory(context.Device, staging.Memory, 0, staging.Size, 0, &mapped).ThrowOnError();
            try
            {
                var data = (ushort*)mapped;
                return new Vector4(
                    (float)BitConverter.UInt16BitsToHalf(data[0]),
                    (float)BitConverter.UInt16BitsToHalf(data[1]),
                    (float)BitConverter.UInt16BitsToHalf(data[2]),
                    (float)BitConverter.UInt16BitsToHalf(data[3]));
            }
            finally
            {
                context.Api.UnmapMemory(context.Device, staging.Memory);
            }
        }
    }

    private void CopyImagePixelToBuffer(VulkanImage image, VulkanBuffer staging, int pixelX, int pixelY)
    {
        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);

        image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(pixelX, pixelY, 0),
            ImageExtent = new Extent3D(1, 1, 1)
        };
        context.Api.CmdCopyImageToBuffer(commandBuffer.InternalHandle, image.InternalHandle, ImageLayout.TransferSrcOptimal, staging.Handle, 1, in region);
        image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        context.SubmitAndWait(commandBuffer);
    }

    public void Dispose()
    {
        outputColorImage?.Dispose();
        albedoImage?.Dispose();
        normalImage?.Dispose();
        cryptoImage?.Dispose();
        positionImage?.Dispose();
        adaptiveStateImage?.Dispose();
        depthImage?.Dispose();
        sceneSettingsBuffer.Dispose();
        indirectCommandsBuffer.Dispose();
        visibleCountsBuffer.Dispose();
        visibleInstanceIdsBuffer.Dispose();
        meshRasterMetadataBuffer.Dispose();
        meshBuffer.Dispose();
        instancesBuffer.Dispose();
        context.Api.DestroySampler(context.Device, previousPositionSampler, default);
        context.Api.DestroyPipeline(context.Device, graphicsPipeline, default);
        context.Api.DestroyPipelineLayout(context.Device, graphicsPipelineLayout, default);
        context.Api.DestroyPipeline(context.Device, shadowOverlayPipeline, default);
        context.Api.DestroyPipeline(context.Device, buildIndirectPipeline, default);
        context.Api.DestroyPipeline(context.Device, cullInstancesPipeline, default);
        context.Api.DestroyPipeline(context.Device, cullResetPipeline, default);
        context.Api.DestroyPipelineLayout(context.Device, computePipelineLayout, default);
        context.Api.DestroyDescriptorSetLayout(context.Device, descriptorSetLayout, default);
    }
}
