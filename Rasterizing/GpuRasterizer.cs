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
    private const int ShadowVirtualPagesPerSide = 32;
    private const int ShadowResidentPagesPerSide = 8;

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
    private readonly VulkanDeviceResources deviceResources;
    private readonly VulkanDescriptorSet descriptorSet;
    private readonly PipelineLayout computePipelineLayout;
    private readonly PipelineLayout graphicsPipelineLayout;
    private readonly Pipeline cullResetPipeline;
    private readonly Pipeline cullInstancesPipeline;
    private readonly Pipeline buildIndirectPipeline;
    private readonly Pipeline deferredLightingPipeline;
    private readonly Pipeline hdriBackgroundPipeline;
    private readonly Pipeline rtAmbientOcclusionPipeline;
    private readonly Pipeline shadowOverlayPipeline;
    private readonly Pipeline graphicsPipeline;
    private readonly Pipeline shadowGraphicsPipeline;
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
    private VulkanImage? previousPositionHistoryImage;
    private VulkanImage? adaptiveStateImage;
    private VulkanImage? shadowMomentImage;
    private VulkanDepthImage? depthImage;
    private VulkanDepthImage? shadowDepthImage;
    private PixelSize renderImageSize;
    private ulong lastBoundColorImageViewHandle;
    private bool settingsDirty = true;
    private bool hasHistory;
    private uint meshCount;
    private MeshRasterMetadataGpu[] meshRasterMetadata = [new()];
    private ulong uploadedMeshesRevision = ulong.MaxValue;
    private ulong uploadedTlasRevision = ulong.MaxValue;
    private ulong uploadedTexturesRevision = ulong.MaxValue;

    public GpuRasterizer(Context context, Scene scene)
    {
        this.context = context;
        this.scene = scene;
        deviceResources = new VulkanDeviceResources(context);
        descriptorSet = CreateDescriptorSet();

        CreateComputeLayoutAndPipelines(out computePipelineLayout, out cullResetPipeline, out cullInstancesPipeline, out buildIndirectPipeline, out deferredLightingPipeline, out hdriBackgroundPipeline, out rtAmbientOcclusionPipeline, out shadowOverlayPipeline);
        CreateGraphicsPipeline(out graphicsPipelineLayout, out graphicsPipeline);
        CreateShadowGraphicsPipeline(graphicsPipelineLayout, out shadowGraphicsPipeline);
        previousPositionSampler = deviceResources.Track(CreatePreviousPositionSampler());

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
        RunVirtualShadowMapPass(commandBuffer, renderData);
        RunRasterPass(commandBuffer);

        RunDeferredLighting(commandBuffer, renderSize);
        RunHdriBackground(commandBuffer, renderSize);

        if (renderData.RenderSettings.RasterRtAmbientOcclusionEnabled != 0)
            RunRtAmbientOcclusion(commandBuffer, renderSize);

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

    private VulkanDescriptorSet CreateDescriptorSet()
    {
        return new VulkanDescriptorSetBuilder()
            .Add(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit)
            .Add(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(5, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(6, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Add(7, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit)
            .Add(8, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit)
            .Add(
                9,
                DescriptorType.CombinedImageSampler,
                MaxTextures,
                ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit,
                DescriptorBindingFlags.PartiallyBoundBit |
                DescriptorBindingFlags.VariableDescriptorCountBit |
                DescriptorBindingFlags.UpdateAfterBindBit)
            .Add(10, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit)
            .Add(11, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit)
            .Add(12, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(13, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(14, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit)
            .Add(15, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Build(context, DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit);
    }

    private void CreateComputeLayoutAndPipelines(out PipelineLayout layout, out Pipeline reset, out Pipeline cull, out Pipeline build, out Pipeline deferred, out Pipeline background, out Pipeline rtAo, out Pipeline shadow)
    {
        var cullPushRange = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Offset = 0, Size = (uint)sizeof(CullPushConstants) };
        Span<DescriptorSetLayout> setLayouts = stackalloc DescriptorSetLayout[1];
        setLayouts[0] = descriptorSet.Layout;
        layout = deviceResources.Track(VulkanPipelineFactory.CreatePipelineLayout(context, setLayouts, cullPushRange));

        reset = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/CullReset.spv"));
        cull = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/CullInstances.spv"));
        build = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/BuildIndirect.spv"));
        deferred = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/DeferredLighting.spv"));
        background = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/HdriBackground.spv"));
        rtAo = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/RtAmbientOcclusion.spv"));
        shadow = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/RtShadowOverlay.spv"));
    }

    private void CreateGraphicsPipeline(out PipelineLayout layout, out Pipeline pipeline)
    {
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(RasterPushConstants)
        };
        Span<DescriptorSetLayout> setLayouts = stackalloc DescriptorSetLayout[1];
        setLayouts[0] = descriptorSet.Layout;
        layout = deviceResources.Track(VulkanPipelineFactory.CreatePipelineLayout(context, setLayouts, pushRange));

        using var shaderModules = VulkanPipelineFactory.CreateShaderModuleSet(
            context,
            "Assets/Shaders/Raster/DrawVS.spv",
            "Assets/Shaders/Raster/DrawFS.spv");
        using var mainName = new ByteString("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = shaderModules.Vertex, PName = mainName };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = shaderModules.Fragment, PName = mainName };

        var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
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
        pipeline = deviceResources.Track(pipeline);

    }

    private void CreateShadowGraphicsPipeline(PipelineLayout layout, out Pipeline pipeline)
    {
        using var shaderModules = VulkanPipelineFactory.CreateShaderModuleSet(
            context,
            "Assets/Shaders/Raster/ShadowVS.spv",
            "Assets/Shaders/Raster/ShadowFS.spv");
        using var mainName = new ByteString("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = shaderModules.Vertex, PName = mainName };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = shaderModules.Fragment, PName = mainName };

        var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
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
        var colorAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment
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

        var colorFormat = Format.R16G16B16A16Sfloat;
        var renderingInfo = new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount = 1,
            PColorAttachmentFormats = &colorFormat,
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
        pipeline = deviceResources.Track(pipeline);

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
        if (previousPositionHistoryImage is not null) context.RetainForExecution(commandBuffer, previousPositionHistoryImage);
        if (shadowMomentImage is not null) context.RetainForExecution(commandBuffer, shadowMomentImage);
        if (depthImage is not null) context.RetainForExecution(commandBuffer, depthImage);
        if (shadowDepthImage is not null) context.RetainForExecution(commandBuffer, shadowDepthImage);

        var supportedHandles = new string[0];
        outputColorImage = new VulkanImage(context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        albedoImage = new VulkanImage(context, (uint)Format.R8G8B8A8Unorm, size, false, supportedHandles);
        normalImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        cryptoImage = new VulkanImage(context, (uint)Format.R32Uint, size, false, supportedHandles);
        positionImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        previousPositionHistoryImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, size, false, supportedHandles);
        adaptiveStateImage = new VulkanImage(context, (uint)Format.R32G32B32A32Sfloat, size, false, supportedHandles);
        depthImage = new VulkanDepthImage(context, DepthFormat, size);
        var shadowAtlasSize = new PixelSize(4096, 4096);
        shadowMomentImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, shadowAtlasSize, false, supportedHandles);
        shadowDepthImage = new VulkanDepthImage(context, DepthFormat, shadowAtlasSize);
        previousPositionHistoryImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
        renderImageSize = size;
        lastBoundColorImageViewHandle = 0;
        hasHistory = false;
    }

    private void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force)
    {
        var revisions = scene.GetResourceRevisions();
        if (!force &&
            uploadedMeshesRevision == revisions.Meshes &&
            uploadedTlasRevision == revisions.Tlas)
            return;

        var instanceBytes = scene.BuildInstanceData();
        var meshBytes = scene.BuildMeshAddressData();
        var rasterMetadataBytes = scene.BuildMeshRasterMetadata();
        meshRasterMetadata = MemoryMarshal.Cast<byte, MeshRasterMetadataGpu>(rasterMetadataBytes).ToArray();

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

        uploadedMeshesRevision = revisions.Meshes;
        uploadedTlasRevision = revisions.Tlas;
        hasHistory = false;
    }

    private void UpdateDescriptorBuffers()
    {
        new VulkanDescriptorWriter()
            .StorageBuffer(0, instancesBuffer)
            .StorageBuffer(7, meshBuffer)
            .StorageBuffer(8, sceneSettingsBuffer)
            .StorageBuffer(10, meshRasterMetadataBuffer)
            .StorageBuffer(11, visibleInstanceIdsBuffer)
            .StorageBuffer(12, visibleCountsBuffer)
            .StorageBuffer(13, indirectCommandsBuffer)
            .Update(context, descriptorSet.Set);
    }

    private void UpdateOutputImageBindings()
    {
        if (outputColorImage is null || lastBoundColorImageViewHandle == outputColorImage.ViewHandle)
            return;

        new VulkanDescriptorWriter()
            .StorageImage(1, outputColorImage)
            .StorageImage(2, albedoImage!)
            .StorageImage(3, normalImage!)
            .StorageImage(4, cryptoImage!)
            .StorageImage(5, positionImage!)
            .StorageImage(6, adaptiveStateImage!)
            .CombinedImageSampler(14, previousPositionSampler, previousPositionHistoryImage!, ImageLayout.ShaderReadOnlyOptimal)
            .StorageImage(15, shadowMomentImage!)
            .Update(context, descriptorSet.Set);

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
            RasterCamera = scene.CaptureRasterCameraData(),
            RasterShadow = scene.CaptureRasterShadowData()
        };
        sceneSettingsBuffer.Upload(StructPacking.ToBytes(new[] { settings }));
        settingsDirty = false;
    }

    private void UpdateTextureBindings()
    {
        var revisions = scene.GetResourceRevisions();
        if (uploadedTexturesRevision == revisions.Textures)
            return;

        var textures = scene.GetTexturesSnapshot();
        if (textures.Length > MaxTextures)
            throw new InvalidOperationException($"Too many textures for bindless descriptor array ({textures.Length} > {MaxTextures}).");

        if (textures.Length == 0)
        {
            uploadedTexturesRevision = revisions.Textures;
            return;
        }

        var descriptors = new DescriptorImageInfo[textures.Length];
        for (var i = 0; i < textures.Length; i++)
            descriptors[i] = textures[i].GetDescriptorImageInfo();

        new VulkanDescriptorWriter()
            .CombinedImageSamplers(9, descriptors)
            .Update(context, descriptorSet.Set);

        uploadedTexturesRevision = revisions.Textures;
    }

    private void RunCullPass(Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData)
    {
        var set = descriptorSet.Set;
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Compute, computePipelineLayout, 0, 1, in set, 0, null);

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

    private void RunVirtualShadowMapPass(Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData)
    {
        if (renderData.RenderSettings.RasterVirtualShadowMapsEnabled == 0 ||
            renderData.Environment.DirectionalIntensity <= 0.0001f ||
            renderData.Environment.DirectionalDirection.LengthSquared() <= 0.0001f)
        {
            shadowMomentImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
            return;
        }

        shadowMomentImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        shadowDepthImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.DepthAttachmentOptimal, AccessFlags.DepthStencilAttachmentWriteBit);

        var momentAttachment = CreateColorAttachment(shadowMomentImage, new ClearValue { Color = new ClearColorValue(1f, 1f, 1f, 1f) });
        var depthClear = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = shadowDepthImage.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = depthClear
        };

        var atlasSize = shadowMomentImage.Size;
        var renderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Math.Max(1, atlasSize.Width), (uint)Math.Max(1, atlasSize.Height)));
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = renderArea,
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &momentAttachment,
            PDepthAttachment = &depthAttachment
        };

        context.Api.CmdBeginRendering(commandBuffer.InternalHandle, in renderingInfo);
        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, shadowGraphicsPipeline);
        var set = descriptorSet.Set;
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, graphicsPipelineLayout, 0, 1, in set, 0, null);

        const int cascadeCount = 4;
        var cascadeWidth = atlasSize.Width / 2;
        var cascadeHeight = atlasSize.Height / 2;
        var physicalPageWidth = Math.Max(1, cascadeWidth / ShadowResidentPagesPerSide);
        var physicalPageHeight = Math.Max(1, cascadeHeight / ShadowResidentPagesPerSide);
        var residentVirtualPageOffset = (ShadowVirtualPagesPerSide - ShadowResidentPagesPerSide) / 2;
        for (var cascade = 0; cascade < cascadeCount; cascade++)
        {
            var cascadeX = (cascade & 1) * cascadeWidth;
            var cascadeY = (cascade >> 1) * cascadeHeight;
            for (var residentPageY = 0; residentPageY < ShadowResidentPagesPerSide; residentPageY++)
            {
                for (var residentPageX = 0; residentPageX < ShadowResidentPagesPerSide; residentPageX++)
                {
                    var physicalPageX = cascadeX + residentPageX * physicalPageWidth;
                    var physicalPageY = cascadeY + residentPageY * physicalPageHeight;
                    var viewport = new Viewport(physicalPageX, physicalPageY, physicalPageWidth, physicalPageHeight, 0f, 1f);
                    var scissor = new Rect2D(
                        new Offset2D(physicalPageX, physicalPageY),
                        new Extent2D((uint)Math.Max(1, physicalPageWidth), (uint)Math.Max(1, physicalPageHeight)));
                    context.Api.CmdSetViewport(commandBuffer.InternalHandle, 0, 1, in viewport);
                    context.Api.CmdSetScissor(commandBuffer.InternalHandle, 0, 1, in scissor);

                    var virtualPageX = residentVirtualPageOffset + residentPageX;
                    var virtualPageY = residentVirtualPageOffset + residentPageY;
                    var packedVirtualPage = (uint)(virtualPageX | (virtualPageY << 16));
                    for (uint meshId = 0; meshId < meshCount; meshId++)
                    {
                        var metadata = meshRasterMetadata[(int)meshId];
                        if (metadata.IndexCount == 0)
                            continue;

                        var push = new RasterPushConstants
                        {
                            MeshId = meshId,
                            VisibleOffset = metadata.VisibleInstanceOffset,
                            Pad0 = (uint)cascade,
                            Pad1 = packedVirtualPage
                        };
                        context.Api.CmdPushConstants(commandBuffer.InternalHandle, graphicsPipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(RasterPushConstants), &push);
                        context.Api.CmdDrawIndirect(commandBuffer.InternalHandle, indirectCommandsBuffer.Handle, meshId * (ulong)StructPacking.SizeOf<DrawIndirectCommandGpu>(), 1, (uint)StructPacking.SizeOf<DrawIndirectCommandGpu>());
                    }
                }
            }
        }

        context.Api.CmdEndRendering(commandBuffer.InternalHandle);
        shadowMomentImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
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
        var set = descriptorSet.Set;
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, graphicsPipelineLayout, 0, 1, in set, 0, null);

        for (uint meshId = 0; meshId < meshCount; meshId++)
        {
            var metadata = meshRasterMetadata[(int)meshId];
            if (metadata.IndexCount == 0)
                continue;

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

        CopyPositionToHistory(commandBuffer);
    }

    private void CopyPositionToHistory(Context.CommandBuffer commandBuffer)
    {
        positionImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
        previousPositionHistoryImage!.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D((uint)Math.Max(1, renderImageSize.Width), (uint)Math.Max(1, renderImageSize.Height), 1)
        };
        context.Api.CmdCopyImage(
            commandBuffer.InternalHandle,
            positionImage.InternalHandle,
            ImageLayout.TransferSrcOptimal,
            previousPositionHistoryImage.InternalHandle,
            ImageLayout.TransferDstOptimal,
            1,
            in region);

        positionImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        previousPositionHistoryImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
    }

    private void RunDeferredLighting(Context.CommandBuffer commandBuffer, PixelSize renderSize)
    {
        RunFullscreenCompute(commandBuffer, renderSize, deferredLightingPipeline);
    }

    private void RunHdriBackground(Context.CommandBuffer commandBuffer, PixelSize renderSize)
    {
        RunFullscreenCompute(commandBuffer, renderSize, hdriBackgroundPipeline);
    }

    private void RunRtAmbientOcclusion(Context.CommandBuffer commandBuffer, PixelSize renderSize)
    {
        RunFullscreenCompute(commandBuffer, renderSize, rtAmbientOcclusionPipeline);
    }

    private void RunShadowOverlay(Context.CommandBuffer commandBuffer, PixelSize renderSize)
    {
        RunFullscreenCompute(commandBuffer, renderSize, shadowOverlayPipeline);
    }

    private void RunFullscreenCompute(Context.CommandBuffer commandBuffer, PixelSize renderSize, Pipeline pipeline)
    {
        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Compute, pipeline);
        var set = descriptorSet.Set;
        context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Compute, computePipelineLayout, 0, 1, in set, 0, null);
        var groupCountX = ((uint)Math.Max(1, renderSize.Width) + ShaderDefines.GROUP_SIZE - 1u) / ShaderDefines.GROUP_SIZE;
        var groupCountY = ((uint)Math.Max(1, renderSize.Height) + ShaderDefines.GROUP_SIZE - 1u) / ShaderDefines.GROUP_SIZE;
        context.Api.CmdDispatch(commandBuffer.InternalHandle, groupCountX, groupCountY, 1);
        InsertMemoryBarrier(commandBuffer.InternalHandle, AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
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

    private void InsertMemoryBarrier(CommandBuffer commandBuffer, AccessFlags srcAccessMask, AccessFlags dstAccessMask)
    {
        VulkanBarriers.Memory(context.Api, commandBuffer, srcAccessMask, dstAccessMask);
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
        previousPositionHistoryImage?.Dispose();
        adaptiveStateImage?.Dispose();
        shadowMomentImage?.Dispose();
        depthImage?.Dispose();
        shadowDepthImage?.Dispose();
        sceneSettingsBuffer.Dispose();
        indirectCommandsBuffer.Dispose();
        visibleCountsBuffer.Dispose();
        visibleInstanceIdsBuffer.Dispose();
        meshRasterMetadataBuffer.Dispose();
        meshBuffer.Dispose();
        instancesBuffer.Dispose();
        deviceResources.Dispose();
        descriptorSet.Dispose();
    }
}
