using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;
using CmdBuf = UniversalUmap.Rendering.Vulkan.Context.CommandBuffer;


namespace UniversalUmap.Rendering.Rasterizing;

internal sealed unsafe class GpuRasterizer : IGpuRenderPath
{
    public int ShaderPixelSizePercent { get; set; } = 100;

    private const uint MaxTextures = 10_000;
    private static readonly Format DepthFormat = Format.D32Sfloat;
    private const int CascadeCount = 4;
    private const int ShadowAtlasSize = 4096;

    private readonly Context context;
    private readonly Scene scene;
    private readonly VulkanDeviceResources deviceResources;
    private readonly VulkanDescriptorSet descriptorSet;

    // Pipelines
    private readonly PipelineLayout computePipelineLayout;
    private readonly PipelineLayout graphicsPipelineLayout;
    private readonly Pipeline cullInstancesPipeline;
    private readonly Pipeline copyAllInstancesPipeline;
    private readonly Pipeline buildIndirectPipeline;
    private readonly Pipeline deferredLightingPipeline;
    private readonly Pipeline hdriBackgroundPipeline;
    private readonly Pipeline temporalResolvePipeline;
    private readonly Pipeline graphicsPipeline;
    private readonly Pipeline shadowGraphicsPipeline;

    // Descriptor-only resources
    private readonly Sampler nearestSampler;
    private readonly Sampler linearSampler;

    // GPU buffers
    private VulkanBuffer instancesBuffer;
    private VulkanBuffer meshBuffer;
    private VulkanBuffer meshRasterMetadataBuffer;
    private VulkanBuffer visibleInstanceIdsBuffer;
    private VulkanBuffer visibleCountsBuffer;
    private VulkanBuffer indirectCommandsBuffer;
    private VulkanBuffer sceneSettingsBuffer;
    private VulkanBuffer probePlaceholderBuffer;

    // GPU images
    private VulkanImage? outputColorImage;
    private VulkanImage? albedoImage;
    private VulkanImage? normalImage;
    private VulkanImage? cryptoImage;
    private VulkanImage? positionImage;
    private VulkanImage? materialImage;
    private VulkanImage? velocityImage;
    private VulkanImage? historyImage;
    private VulkanImage? taaResolveImage;
    private VulkanDepthImage? depthImage;
    private VulkanImage? shadowDepthImage;
    private VulkanDepthImage? shadowDepthTex;
    private PixelSize renderImageSize;

    // Light probes
    private LightProbeSystem? lightProbeSystem;

    // State tracking
    private ulong lastBoundColorViewHandle;
    private bool settingsDirty = true;
    private bool descriptorUpdateNeeded = true;
    private uint meshCount;
    private int frameIndex;
    private ulong uploadedMeshesRevision = ulong.MaxValue;
    private ulong uploadedTlasRevision = ulong.MaxValue;
    private ulong uploadedTexturesRevision = ulong.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowPushConstants { public uint CascadeIndex; public uint pad0; public uint pad1; public uint pad2; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TaaPushConstants { public int Frame; public int Enabled; public float BlendFactor; public int IsMoving; }

    public GpuRasterizer(Context context, Scene scene, LightProbeSystem? lightProbeSystem = null)
    {
        this.context = context;
        this.scene = scene;
        this.lightProbeSystem = lightProbeSystem;
        deviceResources = new VulkanDeviceResources(context);

        descriptorSet = CreateDescriptorSet();
        CreateComputeLayoutAndPipelines(out computePipelineLayout, out cullInstancesPipeline,
            out copyAllInstancesPipeline, out buildIndirectPipeline, out deferredLightingPipeline,
            out hdriBackgroundPipeline, out temporalResolvePipeline);
        CreateGraphicsPipeline(out graphicsPipelineLayout, out graphicsPipeline);
        CreateShadowPipeline(graphicsPipelineLayout, out shadowGraphicsPipeline);

        nearestSampler = deviceResources.Track(CreateSampler(Filter.Nearest));
        linearSampler = deviceResources.Track(CreateSampler(Filter.Linear));

        instancesBuffer = CreatePlaceholder(BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshBuffer = CreatePlaceholder(BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshRasterMetadataBuffer = CreatePlaceholder(BufferUsageFlags.StorageBufferBit);
        visibleInstanceIdsBuffer = CreatePlaceholder(BufferUsageFlags.StorageBufferBit);
        visibleCountsBuffer = CreatePlaceholder(BufferUsageFlags.StorageBufferBit);
        indirectCommandsBuffer = CreatePlaceholder(BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit);
        sceneSettingsBuffer = new VulkanBuffer(context, (ulong)sizeof(SceneSettingsDataGpu),
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        probePlaceholderBuffer = new VulkanBuffer(context, 64, BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        var initCmd = context.CreateCommandBuffer();
        context.BeginCommandBuffer(initCmd);
        UpdateSceneResources(initCmd, true);
        UpdateDescriptorBuffers();
        context.SubmitAndWait(initCmd);

        scene.RenderSettings.PropertyChanged += (_, _) => settingsDirty = true;
        scene.Camera.Changed += () => settingsDirty = true;
        scene.Environment.PropertyChanged += (_, _) => settingsDirty = true;
    }

    public VulkanImage OutputColor => outputColorImage ?? throw new("not initialized");
    public VulkanImage OutputAlbedo => albedoImage ?? throw new("not initialized");
    public VulkanImage OutputNormal => normalImage ?? throw new("not initialized");
    public VulkanImage OutputCrypto => cryptoImage ?? throw new("not initialized");
    public VulkanImage OutputPosition => positionImage ?? throw new("not initialized");
    public VulkanImage OutputAdaptiveState => materialImage ?? throw new("not initialized");
    public PixelSize RenderImageSize => renderImageSize;
    public void Record(PixelSize renderSize, VulkanImage image, Context.CommandBuffer cmd, Scene.RenderDataGpu renderData)
    {
        EnsureRenderImages(renderSize, cmd);
        UpdateSceneResources(cmd, false);

        if (descriptorUpdateNeeded)
        {
            UpdateDescriptorBuffers();
            descriptorUpdateNeeded = false;
        }

        if (lastBoundColorViewHandle != (outputColorImage?.ViewHandle ?? 0))
        {
            UpdateOutputImageBindings();
        }

        var jitterNdc = scene.RenderSettings.TaaEnabled
            ? GetTaaJitterNdc(frameIndex, renderSize)
            : Vector2.Zero;
        UpdateSceneSettingsBuffer(renderData, jitterNdc, scene.RenderSettings.TaaEnabled);

        UpdateTextureBindings();

        FillVisibleCounts(cmd);
        if (scene.RenderSettings.RasterGpuCullingEnabled)
            RunCullInstances(cmd);
        else
            RunCopyAllInstances(cmd);
        RunBuildIndirect(cmd);
        RunShadow(cmd, renderData);
        RunGBuffer(cmd);
        RunDeferred(cmd, renderSize);
        RunBackground(cmd, renderSize);
        RunTAA(cmd, renderSize, renderData);
        CopyTaaResolveToOutputAndHistory(cmd);

        frameIndex++;
        scene.ClearDirty(SceneDirtyFlags.Accumulation | SceneDirtyFlags.Settings);
    }

    public bool QueryPixelUInt(VulkanImage image, int x, int y, out uint value)
    {
        value = 0;
        if (image.CurrentLayout == (uint)ImageLayout.Undefined) return false;
        if (x < 0 || y < 0 || x >= renderImageSize.Width || y >= renderImageSize.Height) return false;
        value = ReadPixelUInt(image, x, y);
        return true;
    }

    public bool QueryPixelHalf4(VulkanImage image, int x, int y, out Vector4 value)
    {
        value = default;
        if (image.CurrentLayout == (uint)ImageLayout.Undefined) return false;
        if (x < 0 || y < 0 || x >= renderImageSize.Width || y >= renderImageSize.Height) return false;
        value = ReadPixelHalf4(image, x, y);
        return true;
    }

    // ─── Pipeline & descriptor creation ─────────────────────────────────────

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
            .Add(9, DescriptorType.CombinedImageSampler, MaxTextures, ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit,
                DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.VariableDescriptorCountBit | DescriptorBindingFlags.UpdateAfterBindBit)
            .Add(10, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit)
            .Add(11, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit)
            .Add(12, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(13, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(14, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit)
            .Add(16, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(18, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(19, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit)
            .Add(20, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit)
            .Add(21, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)
            .Build(context, DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit);
    }

    private void CreateComputeLayoutAndPipelines(out PipelineLayout layout, out Pipeline cull,
        out Pipeline copy, out Pipeline build, out Pipeline deferred, out Pipeline bg, out Pipeline taa)
    {
        var pushRange = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Offset = 0, Size = (uint)sizeof(TaaPushConstants) };
        Span<DescriptorSetLayout> sl = stackalloc DescriptorSetLayout[1]; sl[0] = descriptorSet.Layout;
        layout = deviceResources.Track(VulkanPipelineFactory.CreatePipelineLayout(context, sl, pushRange));

        cull = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/CullInstances.spv"));
        copy = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/CopyAllInstances.spv"));
        build = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/BuildIndirect.spv"));
        deferred = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/DeferredLighting.spv"));
        bg = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/HdriBackground.spv"));
        taa = deviceResources.Track(VulkanPipelineFactory.CreateComputePipeline(context, layout, "Assets/Shaders/Raster/TemporalResolve.spv"));
    }

    private void CreateGraphicsPipeline(out PipelineLayout layout, out Pipeline pipeline)
    {
        var push = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0, Size = (uint)sizeof(ShadowPushConstants) };
        Span<DescriptorSetLayout> sl = stackalloc DescriptorSetLayout[1]; sl[0] = descriptorSet.Layout;
        layout = deviceResources.Track(VulkanPipelineFactory.CreatePipelineLayout(context, sl, push));

        using var sm = VulkanPipelineFactory.CreateShaderModuleSet(context, "Assets/Shaders/Raster/DrawVS.spv", "Assets/Shaders/Raster/DrawFS.spv");
        using var mn = new ByteString("main");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = sm.Vertex, PName = mn };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = sm.Fragment, PName = mn };

        var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var vs = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.BackBit, FrontFace = FrontFace.CounterClockwise, LineWidth = 1f };
        var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.LessOrEqual };

        var nAttachments = 7;
        var ca = stackalloc PipelineColorBlendAttachmentState[nAttachments];
        for (var i = 0; i < nAttachments; i++)
            ca[i] = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = (uint)nAttachments, PAttachments = ca };

        var dyn = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dync = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };

        var cf = stackalloc Format[nAttachments];
        cf[0] = Format.R32G32B32A32Sfloat;
        cf[1] = Format.R8G8B8A8Unorm;
        cf[2] = Format.R16G16B16A16Sfloat;
        cf[3] = Format.R32Uint;
        cf[4] = Format.R16G16B16A16Sfloat;
        cf[5] = Format.R16G16B16A16Sfloat;
        cf[6] = Format.R16G16Sfloat;
        var ri = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = (uint)nAttachments, PColorAttachmentFormats = cf, DepthAttachmentFormat = DepthFormat };

        var pi = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
            PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &vs,
            PRasterizationState = &rs, PMultisampleState = &ms, PDepthStencilState = &ds,
            PColorBlendState = &cb, PDynamicState = &dync, Layout = layout, PNext = &ri
        };
        context.Api.CreateGraphicsPipelines(context.Device, default, 1, in pi, default, out pipeline).ThrowOnError();
        pipeline = deviceResources.Track(pipeline);
    }

    private void CreateShadowPipeline(PipelineLayout layout, out Pipeline pipeline)
    {
        using var sm = VulkanPipelineFactory.CreateShaderModuleSet(context, "Assets/Shaders/Raster/ShadowVS.spv", "Assets/Shaders/Raster/ShadowFS.spv");
        using var mn = new ByteString("main");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = sm.Vertex, PName = mn };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = sm.Fragment, PName = mn };

        var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var vs = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.BackBit, FrontFace = FrontFace.CounterClockwise, LineWidth = 1f };
        var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.LessOrEqual, DepthBoundsTestEnable = false };

        var cf = Format.R32Sfloat;
        var cb = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit };
        var cbs = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &cb };
        var dyn = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dync = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
        var ri = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &cf, DepthAttachmentFormat = DepthFormat };

        var pi = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
            PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &vs,
            PRasterizationState = &rs, PMultisampleState = &ms, PDepthStencilState = &ds,
            PColorBlendState = &cbs, PDynamicState = &dync, Layout = layout, PNext = &ri
        };
        context.Api.CreateGraphicsPipelines(context.Device, default, 1, in pi, default, out pipeline).ThrowOnError();
        pipeline = deviceResources.Track(pipeline);
    }

    private Sampler CreateSampler(Filter filter)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter, MinFilter = filter, MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f
        };
        context.Api.CreateSampler(context.Device, in info, default, out var s).ThrowOnError();
        return s;
    }

    private VulkanBuffer CreatePlaceholder(BufferUsageFlags usage)
        => new(context, 16, usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

    // ─── Resource management ────────────────────────────────────────────────

    private void EnsureRenderImages(PixelSize size, Context.CommandBuffer cmd)
    {
        if (outputColorImage is not null && renderImageSize == size) return;

        void Retain(IDisposable? r) { if (r is not null) context.RetainForExecution(cmd, r); }
        Retain(outputColorImage); Retain(albedoImage); Retain(normalImage); Retain(cryptoImage);
        Retain(positionImage); Retain(materialImage); Retain(velocityImage); Retain(historyImage); Retain(taaResolveImage);
        Retain(depthImage); Retain(shadowDepthImage); Retain(shadowDepthTex);

        outputColorImage = new VulkanImage(context, (uint)Format.R32G32B32A32Sfloat, size, false, []);
        albedoImage = new VulkanImage(context, (uint)Format.R8G8B8A8Unorm, size, false, []);
        normalImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, size, false, []);
        cryptoImage = new VulkanImage(context, (uint)Format.R32Uint, size, false, []);
        positionImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, size, false, []);
        materialImage = new VulkanImage(context, (uint)Format.R16G16B16A16Sfloat, size, false, []);
        velocityImage = new VulkanImage(context, (uint)Format.R16G16Sfloat, size, false, []);
        historyImage = new VulkanImage(context, (uint)Format.R32G32B32A32Sfloat, size, false, []);
        taaResolveImage = new VulkanImage(context, (uint)Format.R32G32B32A32Sfloat, size, false, []);
        depthImage = new VulkanDepthImage(context, DepthFormat, size);
        shadowDepthImage = new VulkanImage(context, (uint)Format.R32Sfloat, new PixelSize(ShadowAtlasSize, ShadowAtlasSize), false, []);
        shadowDepthTex = new VulkanDepthImage(context, DepthFormat, new PixelSize(ShadowAtlasSize, ShadowAtlasSize));

        historyImage.TransitionLayout(cmd.InternalHandle, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
        renderImageSize = size;
        lastBoundColorViewHandle = 0;
        frameIndex = 0;
        settingsDirty = true;
    }

    private VulkanBuffer UploadDeviceLocal(Context.CommandBuffer cmd, ReadOnlySpan<byte> data, BufferUsageFlags usage)
    {
        var dest = new VulkanBuffer(context, (ulong)data.Length, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        var staging = new VulkanBuffer(context, (ulong)data.Length, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, data);
        var copyRegion = new BufferCopy { Size = (ulong)data.Length };
        context.Api.CmdCopyBuffer(cmd.InternalHandle, staging.Handle, dest.Handle, 1, in copyRegion);
        context.RetainForExecution(cmd, staging);
        return dest;
    }

    private VulkanBuffer CreateDeviceLocal(Context.CommandBuffer cmd, ulong size, BufferUsageFlags usage)
    {
        var dest = new VulkanBuffer(context, size, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        return dest;
    }

    private void UpdateSceneResources(Context.CommandBuffer cmd, bool force)
    {
        var rev = scene.GetResourceRevisions();
        if (!force && uploadedMeshesRevision == rev.Meshes && uploadedTlasRevision == rev.Tlas) return;

        var instBytes = scene.BuildInstanceData();
        var meshBytes = scene.BuildMeshAddressData();
        var metaBytes = scene.BuildMeshRasterMetadata();

        var prevInst = instancesBuffer; var prevMesh = meshBuffer;
        var prevMeta = meshRasterMetadataBuffer; var prevVis = visibleInstanceIdsBuffer;
        var prevCnt = visibleCountsBuffer; var prevInd = indirectCommandsBuffer;

        instancesBuffer = UploadDeviceLocal(cmd, instBytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshBuffer = UploadDeviceLocal(cmd, meshBytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshRasterMetadataBuffer = UploadDeviceLocal(cmd, metaBytes, BufferUsageFlags.StorageBufferBit);

        var instCount = (uint)Math.Max(1, instBytes.Length / sizeof(InstanceGpu));
        meshCount = (uint)Math.Max(1, metaBytes.Length / sizeof(MeshRasterMetadataGpu));

        visibleInstanceIdsBuffer = CreateDeviceLocal(cmd, instCount * sizeof(uint), BufferUsageFlags.StorageBufferBit);
        visibleCountsBuffer = CreateDeviceLocal(cmd, Math.Max(1u, meshCount) * sizeof(uint), BufferUsageFlags.StorageBufferBit);
        indirectCommandsBuffer = CreateDeviceLocal(cmd, Math.Max(1u, meshCount) * (ulong)sizeof(DrawIndirectCommandGpu), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit);

        MemoryBarrier(cmd, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

        void R(IDisposable? r) { if (r is not null) context.RetainForExecution(cmd, r); }
        R(prevInst); R(prevMesh); R(prevMeta); R(prevVis); R(prevCnt); R(prevInd);

        descriptorUpdateNeeded = true;
        uploadedMeshesRevision = rev.Meshes; uploadedTlasRevision = rev.Tlas;
    }

    private void UpdateDescriptorBuffers()
    {
        var writer = new VulkanDescriptorWriter()
            .StorageBuffer(0, instancesBuffer)
            .StorageBuffer(7, meshBuffer)
            .StorageBuffer(8, sceneSettingsBuffer)
            .StorageBuffer(10, meshRasterMetadataBuffer)
            .StorageBuffer(11, visibleInstanceIdsBuffer)
            .StorageBuffer(12, visibleCountsBuffer)
            .StorageBuffer(13, indirectCommandsBuffer);

        if (lightProbeSystem is not null && lightProbeSystem.ProbeCount > 0)
            writer.StorageBuffer(16, lightProbeSystem.ProbeBuffer);
        else
            writer.StorageBuffer(16, probePlaceholderBuffer);

        writer.Update(context, descriptorSet.Set);
    }

    private void UpdateOutputImageBindings()
    {
        if (outputColorImage is null || lastBoundColorViewHandle == outputColorImage.ViewHandle) return;

        new VulkanDescriptorWriter()
            .StorageImage(1, outputColorImage)
            .StorageImage(2, albedoImage!)
            .StorageImage(3, normalImage!)
            .StorageImage(4, cryptoImage!)
            .StorageImage(5, positionImage!)
            .StorageImage(6, materialImage!)
            .StorageImage(14, shadowDepthImage!)
            .CombinedImageSampler(19, nearestSampler, velocityImage!, ImageLayout.ShaderReadOnlyOptimal)
            .CombinedImageSampler(20, nearestSampler, historyImage!, ImageLayout.ShaderReadOnlyOptimal)
            .StorageImage(21, taaResolveImage!)
            .Update(context, descriptorSet.Set);
        lastBoundColorViewHandle = outputColorImage.ViewHandle;
    }

    private void UpdateSceneSettingsBuffer(Scene.RenderDataGpu renderData, Vector2 jitterNdc, bool force = false)
    {
        if (!settingsDirty && !force) return;

        var probeGrid = new LightProbeGridDataGpu
        {
            GridDimX = 0, GridDimY = 0, GridDimZ = 0
        };
        if (lightProbeSystem is not null && lightProbeSystem.ProbeCount > 0)
        {
            probeGrid = new LightProbeGridDataGpu
            {
                GridMin = lightProbeSystem.GridMin,
                GridMax = lightProbeSystem.GridMax,
                GridDimX = 8,
                GridDimY = 8,
                GridDimZ = 8
            };
        }

        var s = new SceneSettingsDataGpu
        {
            RenderSettings = renderData.RenderSettings,
            Environment = renderData.Environment,
            RasterCamera = scene.CaptureRasterCameraData(jitterNdc),
            RasterShadow = scene.CaptureRasterShadowData(),
            LightProbeGrid = probeGrid
        };
        sceneSettingsBuffer.Upload(StructPacking.ToBytes(new[] { s }));
        settingsDirty = false;
    }

    private void UpdateTextureBindings()
    {
        var rev = scene.GetResourceRevisions();
        if (uploadedTexturesRevision == rev.Textures) return;

        var textures = scene.GetTexturesSnapshot();
        if (textures.Length == 0) { uploadedTexturesRevision = rev.Textures; return; }

        var desc = new DescriptorImageInfo[textures.Length];
        for (var i = 0; i < textures.Length; i++) desc[i] = textures[i].GetDescriptorImageInfo();

        new VulkanDescriptorWriter().CombinedImageSamplers(9, desc).Update(context, descriptorSet.Set);
        uploadedTexturesRevision = rev.Textures;
    }

    // ─── Render passes ──────────────────────────────────────────────────────

    private void BindDescriptorSet(CmdBuf cmd, PipelineBindPoint point, PipelineLayout layout)
    {
        var set = descriptorSet.Set;
        context.Api.CmdBindDescriptorSets(cmd.InternalHandle, point, layout, 0, 1, in set, 0, null);
    }

    private void MemoryBarrier(CmdBuf cmd, AccessFlags src, AccessFlags dst)
        => VulkanBarriers.Memory(context.Api, cmd.InternalHandle, src, dst);

    private static Vector2 GetTaaJitterNdc(int frame, PixelSize size)
    {
        var sampleIndex = (frame & 1023) + 1;
        var jitterPixels = new Vector2(
            Halton(sampleIndex, 2) - 0.5f,
            Halton(sampleIndex, 3) - 0.5f);

        return new Vector2(
            2.0f * jitterPixels.X / Math.Max(size.Width, 1),
            2.0f * jitterPixels.Y / Math.Max(size.Height, 1));
    }

    private static float Halton(int index, int basis)
    {
        var result = 0.0f;
        var fraction = 1.0f / basis;
        while (index > 0)
        {
            result += fraction * (index % basis);
            index /= basis;
            fraction /= basis;
        }

        return result;
    }

    private void FillVisibleCounts(CmdBuf cmd)
    {
        context.Api.CmdFillBuffer(cmd.InternalHandle, visibleCountsBuffer.Handle, 0, visibleCountsBuffer.Size, 0);
        MemoryBarrier(cmd, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    private void RunCullInstances(CmdBuf cmd)
    {
        BindDescriptorSet(cmd, PipelineBindPoint.Compute, computePipelineLayout);
        context.Api.CmdBindPipeline(cmd.InternalHandle, PipelineBindPoint.Compute, cullInstancesPipeline);
        var instCount = (uint)Math.Max(1, (int)(instancesBuffer.Size / (ulong)sizeof(InstanceGpu)));
        context.Api.CmdDispatch(cmd.InternalHandle, (instCount + 15u) / 16u, 1, 1);
        MemoryBarrier(cmd, AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    private void RunCopyAllInstances(CmdBuf cmd)
    {
        BindDescriptorSet(cmd, PipelineBindPoint.Compute, computePipelineLayout);
        context.Api.CmdBindPipeline(cmd.InternalHandle, PipelineBindPoint.Compute, copyAllInstancesPipeline);
        var instCount = (uint)Math.Max(1, (int)(instancesBuffer.Size / (ulong)sizeof(InstanceGpu)));
        context.Api.CmdDispatch(cmd.InternalHandle, (instCount + 15u) / 16u, 1, 1);
        MemoryBarrier(cmd, AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    private void RunBuildIndirect(CmdBuf cmd)
    {
        BindDescriptorSet(cmd, PipelineBindPoint.Compute, computePipelineLayout);
        context.Api.CmdBindPipeline(cmd.InternalHandle, PipelineBindPoint.Compute, buildIndirectPipeline);
        context.Api.CmdDispatch(cmd.InternalHandle, Math.Max(1u, meshCount + 15u) / 16u, 1, 1);
        MemoryBarrier(cmd, AccessFlags.ShaderWriteBit, AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderReadBit);
    }

    private void RunShadow(CmdBuf cmd, Scene.RenderDataGpu renderData)
    {
        bool shadowsOn =
            renderData.Environment.DirectionalIntensity > 0.0001f &&
            renderData.Environment.DirectionalDirection.LengthSquared() > 0.0001f;

        if (!shadowsOn)
        {
            shadowDepthImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
            return;
        }

        shadowDepthImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        shadowDepthTex!.TransitionLayout(cmd.InternalHandle, ImageLayout.DepthAttachmentOptimal, AccessFlags.DepthStencilAttachmentWriteBit);

        var atlasSize = ShadowAtlasSize;
        var cascadeW = atlasSize / 2;
        var cascadeH = atlasSize / 2;
        var depthClear = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };

        for (int cascade = 0; cascade < CascadeCount; cascade++)
        {
            int cx = (cascade & 1) * cascadeW;
            int cy = (cascade >> 1) * cascadeH;
            var clear = new ClearValue { Color = new ClearColorValue(1f, 0f, 0f, 0f) };
            var colorAtt = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = new ImageView(shadowDepthImage.ViewHandle),
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
                ClearValue = clear
            };
            var depthAtt = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = shadowDepthTex.View,
                ImageLayout = ImageLayout.DepthAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
                ClearValue = depthClear
            };

            var renderArea = new Rect2D(new Offset2D(cx, cy), new Extent2D((uint)cascadeW, (uint)cascadeH));
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = renderArea,
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAtt,
                PDepthAttachment = &depthAtt
            };

            context.Api.CmdBeginRendering(cmd.InternalHandle, in renderingInfo);
            context.Api.CmdBindPipeline(cmd.InternalHandle, PipelineBindPoint.Graphics, shadowGraphicsPipeline);
            BindDescriptorSet(cmd, PipelineBindPoint.Graphics, graphicsPipelineLayout);

            var viewport = new Viewport(cx, cy, cascadeW, cascadeH, 0f, 1f);
            var scissor = new Rect2D(new Offset2D(cx, cy), new Extent2D((uint)cascadeW, (uint)cascadeH));
            context.Api.CmdSetViewport(cmd.InternalHandle, 0, 1, in viewport);
            context.Api.CmdSetScissor(cmd.InternalHandle, 0, 1, in scissor);

            var shadowPush = new ShadowPushConstants { CascadeIndex = (uint)cascade };
            context.Api.CmdPushConstants(cmd.InternalHandle, graphicsPipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(ShadowPushConstants), &shadowPush);
            if (meshCount > 0)
                context.Api.CmdDrawIndirect(cmd.InternalHandle, indirectCommandsBuffer.Handle, 0, meshCount, (uint)sizeof(DrawIndirectCommandGpu));

            context.Api.CmdEndRendering(cmd.InternalHandle);
        }

        shadowDepthImage.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit);
    }

    private void RunGBuffer(CmdBuf cmd)
    {
        TransitionGBufferImagesForRendering(cmd);

        var attachments = stackalloc RenderingAttachmentInfo[7];
        var zero = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };
        var cryptoClear = new ClearValue { Color = new ClearColorValue(ShaderDefines.INVALID_INSTANCE, 0f, 0f, 0f) };
        attachments[0] = MakeAttachment(outputColorImage!, zero);
        attachments[1] = MakeAttachment(albedoImage!, zero);
        attachments[2] = MakeAttachment(normalImage!, zero);
        attachments[3] = MakeAttachment(cryptoImage!, cryptoClear);
        attachments[4] = MakeAttachment(positionImage!, zero);
        attachments[5] = MakeAttachment(materialImage!, zero);
        attachments[6] = MakeAttachment(velocityImage!, zero);

        var depthClear = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        var depthAtt = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depthImage!.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
            ClearValue = depthClear
        };

        var rArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)renderImageSize.Width, (uint)renderImageSize.Height));
        var rInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = rArea, LayerCount = 1,
            ColorAttachmentCount = 7, PColorAttachments = attachments,
            PDepthAttachment = &depthAtt
        };

        context.Api.CmdBeginRendering(cmd.InternalHandle, in rInfo);

        var vp = new Viewport(0, 0, renderImageSize.Width, renderImageSize.Height, 0f, 1f);
        context.Api.CmdSetViewport(cmd.InternalHandle, 0, 1, in vp);
        context.Api.CmdSetScissor(cmd.InternalHandle, 0, 1, in rArea);
        context.Api.CmdBindPipeline(cmd.InternalHandle, PipelineBindPoint.Graphics, graphicsPipeline);
        BindDescriptorSet(cmd, PipelineBindPoint.Graphics, graphicsPipelineLayout);

        if (meshCount > 0)
            context.Api.CmdDrawIndirect(cmd.InternalHandle, indirectCommandsBuffer.Handle, 0, meshCount, (uint)sizeof(DrawIndirectCommandGpu));

        context.Api.CmdEndRendering(cmd.InternalHandle);

        TransitionGBufferImagesForCompute(cmd);
    }

    private void RunDeferred(CmdBuf cmd, PixelSize size)
    {
        RunFullscreenCompute(cmd, size, deferredLightingPipeline);
    }

    private void RunBackground(CmdBuf cmd, PixelSize size)
    {
        RunFullscreenCompute(cmd, size, hdriBackgroundPipeline);
    }

    private void RunTAA(CmdBuf cmd, PixelSize size, Scene.RenderDataGpu renderData)
    {
        taaResolveImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderWriteBit);
        var push = new TaaPushConstants
        {
            Frame = frameIndex,
            Enabled = scene.RenderSettings.TaaEnabled ? 1 : 0,
            BlendFactor = renderData.IsMoving != 0 ? 0.65f : 0.9f,
            IsMoving = renderData.IsMoving
        };
        context.Api.CmdBindPipeline(cmd.InternalHandle, PipelineBindPoint.Compute, temporalResolvePipeline);
        var set = descriptorSet.Set;
        context.Api.CmdBindDescriptorSets(cmd.InternalHandle, PipelineBindPoint.Compute, computePipelineLayout, 0, 1, in set, 0, null);
        context.Api.CmdPushConstants(cmd.InternalHandle, computePipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(TaaPushConstants), &push);

        var gx = ((uint)size.Width + 15u) / 16u;
        var gy = ((uint)size.Height + 15u) / 16u;
        context.Api.CmdDispatch(cmd.InternalHandle, gx, gy, 1);
        MemoryBarrier(cmd, AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    private void CopyTaaResolveToOutputAndHistory(CmdBuf cmd)
    {
        taaResolveImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
        outputColorImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit);
        historyImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit);

        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D((uint)renderImageSize.Width, (uint)renderImageSize.Height, 1)
        };

        context.Api.CmdCopyImage(
            cmd.InternalHandle,
            taaResolveImage.InternalHandle,
            ImageLayout.TransferSrcOptimal,
            outputColorImage.InternalHandle,
            ImageLayout.TransferDstOptimal,
            1,
            in copy);

        context.Api.CmdCopyImage(
            cmd.InternalHandle,
            taaResolveImage.InternalHandle,
            ImageLayout.TransferSrcOptimal,
            historyImage.InternalHandle,
            ImageLayout.TransferDstOptimal,
            1,
            in copy);

        taaResolveImage.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderWriteBit);
        outputColorImage.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        historyImage.TransitionLayout(cmd.InternalHandle, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
    }

    private void RunFullscreenCompute(CmdBuf cmd, PixelSize size, Pipeline pipeline)
    {
        context.Api.CmdBindPipeline(cmd.InternalHandle, PipelineBindPoint.Compute, pipeline);
        BindDescriptorSet(cmd, PipelineBindPoint.Compute, computePipelineLayout);
        var gx = ((uint)size.Width + 15u) / 16u;
        var gy = ((uint)size.Height + 15u) / 16u;
        context.Api.CmdDispatch(cmd.InternalHandle, gx, gy, 1);
        MemoryBarrier(cmd, AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    private void TransitionGBufferImagesForRendering(CmdBuf cmd)
    {
        outputColorImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        albedoImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        normalImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        cryptoImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        positionImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        materialImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        velocityImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);
        depthImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.DepthAttachmentOptimal, AccessFlags.DepthStencilAttachmentWriteBit);
    }

    private void TransitionGBufferImagesForCompute(CmdBuf cmd)
    {
        outputColorImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        albedoImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        normalImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        cryptoImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        positionImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        materialImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        velocityImage!.TransitionLayout(cmd.InternalHandle, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
    }

    private static RenderingAttachmentInfo MakeAttachment(VulkanImage img, ClearValue cv)
    {
        return new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = new ImageView(img.ViewHandle),
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
            ClearValue = cv
        };
    }

    // ─── Pixel queries ──────────────────────────────────────────────────────

    private uint ReadPixelUInt(VulkanImage image, int px, int py)
    {
        using var staging = new VulkanBuffer(context, sizeof(uint), BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        CopyPixel(image, staging, px, py);
        void* mapped; context.Api.MapMemory(context.Device, staging.Memory, 0, staging.Size, 0, &mapped).ThrowOnError();
        var val = *(uint*)mapped;
        context.Api.UnmapMemory(context.Device, staging.Memory);
        return val;
    }

    private Vector4 ReadPixelHalf4(VulkanImage image, int px, int py)
    {
        using var staging = new VulkanBuffer(context, sizeof(ushort) * 4, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        CopyPixel(image, staging, px, py);
        void* mapped; context.Api.MapMemory(context.Device, staging.Memory, 0, staging.Size, 0, &mapped).ThrowOnError();
        var data = (ushort*)mapped;
        var v = new Vector4(
            (float)BitConverter.UInt16BitsToHalf(data[0]),
            (float)BitConverter.UInt16BitsToHalf(data[1]),
            (float)BitConverter.UInt16BitsToHalf(data[2]),
            (float)BitConverter.UInt16BitsToHalf(data[3]));
        context.Api.UnmapMemory(context.Device, staging.Memory);
        return v;
    }

    private void CopyPixel(VulkanImage image, VulkanBuffer staging, int px, int py)
    {
        var cb = context.CreateCommandBuffer();
        context.BeginCommandBuffer(cb);
        image.TransitionLayout(cb.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(px, py, 0),
            ImageExtent = new Extent3D(1, 1, 1)
        };
        context.Api.CmdCopyImageToBuffer(cb.InternalHandle, image.InternalHandle, ImageLayout.TransferSrcOptimal, staging.Handle, 1, in region);
        image.TransitionLayout(cb.InternalHandle, ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        context.SubmitAndWait(cb);
    }

    // ─── Cleanup ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        outputColorImage?.Dispose(); albedoImage?.Dispose(); normalImage?.Dispose();
        cryptoImage?.Dispose(); positionImage?.Dispose(); materialImage?.Dispose();
        velocityImage?.Dispose(); historyImage?.Dispose(); taaResolveImage?.Dispose();
        depthImage?.Dispose(); shadowDepthImage?.Dispose(); shadowDepthTex?.Dispose();
        sceneSettingsBuffer.Dispose(); probePlaceholderBuffer.Dispose();
        indirectCommandsBuffer.Dispose(); visibleCountsBuffer.Dispose();
        visibleInstanceIdsBuffer.Dispose(); meshRasterMetadataBuffer.Dispose();
        meshBuffer.Dispose(); instancesBuffer.Dispose();
        deviceResources.Dispose(); descriptorSet.Dispose();
    }
}
