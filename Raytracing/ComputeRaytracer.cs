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
    private readonly PipelineBundle pathTracingPipelines;

    private VulkanBuffer instancesBuffer;
    private VulkanBuffer meshBuffer;
    private ulong uploadedMeshesRevision = ulong.MaxValue;
    private ulong uploadedTlasRevision = ulong.MaxValue;

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

        Span<DescriptorSetLayout> setLayouts = stackalloc DescriptorSetLayout[2];
        setLayouts[0] = primaryDescriptorSetLayout;
        setLayouts[1] = localWavefrontDescriptorSetLayout;

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)sizeof(PushDataGpu)
        };
        var pipelineLayoutLocal = DeviceResources.Track(VulkanPipelineFactory.CreatePipelineLayout(Context, setLayouts, pushConstantRange));

        var aoPipelineBundle = CreatePipelineBundle("Ao", "Compute", pipelineLayoutLocal, mainName);
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
        pathTracingPipelines = pathTracingPipelineBundle;
        Log.Information("Compute raytracer pipelines and descriptors created for AO and Path Tracing.");

        var initCommandBuffer = Context.CreateCommandBuffer();
        Context.BeginCommandBuffer(initCommandBuffer);
        UpdateSceneResources(initCommandBuffer, force: true);
        Context.SubmitAndWait(initCommandBuffer);
    }

    protected override void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force)
    {
        var revisions = Scene.GetResourceRevisions();
        var meshesDirty = force || uploadedMeshesRevision != revisions.Meshes;
        var tlasDirty = force || uploadedTlasRevision != revisions.Tlas;
        if (!meshesDirty && !tlasDirty && uploadedLightsRevision == revisions.Lights)
            return;

        if (meshesDirty || tlasDirty)
        {
            UpdateMeshSceneBuffers(commandBuffer, ref instancesBuffer, ref meshBuffer, descriptorSet);
            uploadedMeshesRevision = revisions.Meshes;
            uploadedTlasRevision = revisions.Tlas;
        }

        UpdateLightBindings(commandBuffer);
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
            RenderMode.PathTracing => pathTracingPipelines,
            _ => pathTracingPipelines
        };
    }

    public override void Dispose()
    {
        DisposeCommonResources();
        DisposeWavefrontResources();
        meshBuffer.Dispose();
        instancesBuffer.Dispose();

        DeviceResources.Dispose();
    }
}
