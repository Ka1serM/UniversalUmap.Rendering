using Avalonia;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Raytracing;

internal sealed unsafe class ComputeRaytracer : GpuRaytracer
{
    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly DescriptorSet descriptorSet;
    private readonly DescriptorSetLayout wavefrontDescriptorSetLayout;
    private readonly DescriptorSet wavefrontBootstrapDescriptorSet;
    private readonly DescriptorSet wavefrontForwardDescriptorSet;
    private readonly DescriptorSet wavefrontReverseDescriptorSet;
    private readonly PipelineLayout pipelineLayout;
    private readonly PipelineBundle aoPipelines;
    private readonly PipelineBundle directLightingPipelines;
    private readonly PipelineBundle pathTracingPipelines;

    private VulkanBuffer instancesBuffer;
    private VulkanBuffer meshBuffer;

    public ComputeRaytracer(Context context, Scene scene)
        : base(context, scene)
    {
        using var mainName = new ByteString("main");

        CreatePrimaryDescriptorSet(out var primaryDescriptorSetLayout, out var primaryDescriptorSet);
        CreateWavefrontDescriptorSets(
            out var localWavefrontDescriptorSetLayout,
            out var localWavefrontBootstrapDescriptorSet,
            out var localWavefrontForwardDescriptorSet,
            out var localWavefrontReverseDescriptorSet);

        var setLayouts = stackalloc DescriptorSetLayout[2];
        setLayouts[0] = primaryDescriptorSetLayout;
        setLayouts[1] = localWavefrontDescriptorSetLayout;

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)sizeof(PushDataGpu)
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };
        Context.Api.CreatePipelineLayout(Context.Device, in pipelineLayoutInfo, default, out var pipelineLayoutLocal).ThrowOnError();

        var aoPipelineBundle = CreatePipelineBundle("Ao", "Compute", pipelineLayoutLocal, mainName);
        var directLightingPipelineBundle = CreatePipelineBundle("Direct", "Compute", pipelineLayoutLocal, mainName);
        var pathTracingPipelineBundle = CreatePipelineBundle("Path", "Compute", pipelineLayoutLocal, mainName);

        instancesBuffer = CreateDeviceLocalBuffer(16, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshBuffer = CreateDeviceLocalBuffer(16, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        InitializeWavefrontBuffers();

        descriptorSetLayout = primaryDescriptorSetLayout;
        descriptorSet = primaryDescriptorSet;
        wavefrontDescriptorSetLayout = localWavefrontDescriptorSetLayout;
        wavefrontBootstrapDescriptorSet = localWavefrontBootstrapDescriptorSet;
        wavefrontForwardDescriptorSet = localWavefrontForwardDescriptorSet;
        wavefrontReverseDescriptorSet = localWavefrontReverseDescriptorSet;
        pipelineLayout = pipelineLayoutLocal;
        aoPipelines = aoPipelineBundle;
        directLightingPipelines = directLightingPipelineBundle;
        pathTracingPipelines = pathTracingPipelineBundle;
        Log.Information("Compute raytracer pipelines and descriptors created for AO, Direct Lighting, and Path Tracing.");

        var initCommandBuffer = Context.CreateCommandBuffer();
        Context.BeginCommandBuffer(initCommandBuffer);
        UpdateSceneResources(initCommandBuffer, force: true);
        Context.SubmitAndWait(initCommandBuffer);
    }

    protected override void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force)
    {
        if (!force && !Scene.IsDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas))
            return;

        UpdateMeshSceneBuffers(commandBuffer, ref instancesBuffer, ref meshBuffer, descriptorSet);

        Scene.ClearDirty(SceneDirtyFlags.Meshes | SceneDirtyFlags.Tlas);
        Log.Information(
            "Compute scene resources updated: instancesBytes={InstancesBytes}, meshBytes={MeshBytes}.",
            instancesBuffer.Size,
            meshBuffer.Size);
    }

    protected override void ExecuteRaytracing(Context.CommandBuffer commandBuffer, VulkanImage image, PushDataGpu pushConstants)
    {
        var boundDescriptorSets = stackalloc DescriptorSet[2];
        boundDescriptorSets[0] = descriptorSet;
        boundDescriptorSets[1] = wavefrontBootstrapDescriptorSet;

        ExecuteWavefrontPass(
            commandBuffer,
            image,
            pushConstants,
            pipelineLayout,
            wavefrontBootstrapDescriptorSet,
            wavefrontForwardDescriptorSet,
            wavefrontReverseDescriptorSet,
            2,
            boundDescriptorSets,
            GetPipelineBundle(),
            "Compute");
    }

    protected override DescriptorSet GetDescriptorSet() => descriptorSet;
    protected override DescriptorSetLayout GetDescriptorSetLayout() => descriptorSetLayout;

    private PipelineBundle GetPipelineBundle()
    {
        return EffectiveRenderMode switch
        {
            RenderMode.AmbientOcclusion => aoPipelines,
            RenderMode.DirectLighting => directLightingPipelines,
            RenderMode.PathTracing => pathTracingPipelines,
            _ => directLightingPipelines
        };
    }

    public override void Dispose()
    {
        DisposeCommonResources();
        DisposeWavefrontResources();
        meshBuffer.Dispose();
        instancesBuffer.Dispose();

        DestroyPipelineBundle(pathTracingPipelines);
        DestroyPipelineBundle(directLightingPipelines);
        DestroyPipelineBundle(aoPipelines);
        if (pipelineLayout.Handle != default)
            Context.Api.DestroyPipelineLayout(Context.Device, pipelineLayout, default);
        if (wavefrontDescriptorSetLayout.Handle != default)
            Context.Api.DestroyDescriptorSetLayout(Context.Device, wavefrontDescriptorSetLayout, default);
        if (descriptorSetLayout.Handle != default)
            Context.Api.DestroyDescriptorSetLayout(Context.Device, descriptorSetLayout, default);
    }
}
