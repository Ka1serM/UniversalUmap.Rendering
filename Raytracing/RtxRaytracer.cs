using System;
using System.Runtime.InteropServices;
using Avalonia;
using Serilog;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Raytracing;

internal sealed unsafe class RtxRaytracer : GpuRaytracer
{
    private const int TlasBufferCount = 2;

    private sealed class TlasResourceSlot : IDisposable
    {
        public readonly Accel Tlas;
        public VulkanBuffer InstancesBuffer;

        public TlasResourceSlot(Context context, KhrAccelerationStructure accelExt)
        {
            Tlas = new Accel(context, accelExt);
            InstancesBuffer = new VulkanBuffer(
                context,
                16,
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit);
        }

        public void Dispose()
        {
            InstancesBuffer.Dispose();
            Tlas.Dispose();
        }
    }

    private readonly KhrAccelerationStructure accelExt;

    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly DescriptorSet descriptorSet;
    private readonly DescriptorSetLayout wavefrontDescriptorSetLayout;
    private readonly DescriptorSet wavefrontBootstrapDescriptorSet;
    private readonly DescriptorSet wavefrontForwardDescriptorSet;
    private readonly DescriptorSet wavefrontReverseDescriptorSet;
    private readonly DescriptorSetLayout accelDescriptorSetLayout;
    private readonly DescriptorSet accelDescriptorSet;
    private readonly PipelineLayout pipelineLayout;
    private readonly PipelineBundle aoPipelines;
    private readonly PipelineBundle pathTracingPipelines;
    private readonly TlasResourceSlot[] tlasSlots;

    private int activeTlasSlotIndex;
    private VulkanBuffer instancesBuffer;
    private VulkanBuffer meshBuffer;
    private ulong uploadedMeshesRevision = ulong.MaxValue;
    private ulong uploadedTlasRevision = ulong.MaxValue;

    public RtxRaytracer(Context context, Scene scene)
        : base(context, scene)
    {
        if (!Context.Api.TryGetDeviceExtension(Context.Instance, Context.Device, out accelExt))
            throw new InvalidOperationException("VK_KHR_acceleration_structure extension is not available.");

        using var mainName = new ByteString("main");

        CreatePrimaryDescriptorSet(out var primaryDescriptorSetLayout, out var primaryDescriptorSet);
        CreateWavefrontDescriptorSets(
            out var localWavefrontDescriptorSetLayout,
            out var localWavefrontBootstrapDescriptorSet,
            out var localWavefrontForwardDescriptorSet,
            out var localWavefrontReverseDescriptorSet);
        CreateAccelDescriptorSet(out var localAccelDescriptorSetLayout, out var localAccelDescriptorSet);

        Span<DescriptorSetLayout> setLayouts = stackalloc DescriptorSetLayout[3];
        setLayouts[0] = primaryDescriptorSetLayout;
        setLayouts[1] = localWavefrontDescriptorSetLayout;
        setLayouts[2] = localAccelDescriptorSetLayout;

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)sizeof(PushDataGpu)
        };
        var pipelineLayoutLocal = DeviceResources.Track(VulkanPipelineFactory.CreatePipelineLayout(Context, setLayouts, pushConstantRange));

        aoPipelines = CreatePipelineBundle("Ao", "Rtx", pipelineLayoutLocal, mainName);
        pathTracingPipelines = CreatePipelineBundle("Path", "Rtx", pipelineLayoutLocal, mainName);

        instancesBuffer = CreateDeviceLocalBuffer(16, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        meshBuffer = CreateDeviceLocalBuffer(16, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit);
        InitializeWavefrontBuffers();

        tlasSlots = new TlasResourceSlot[TlasBufferCount];
        for (var i = 0; i < tlasSlots.Length; i++)
            tlasSlots[i] = new TlasResourceSlot(Context, accelExt);

        descriptorSetLayout = primaryDescriptorSetLayout;
        descriptorSet = primaryDescriptorSet;
        wavefrontDescriptorSetLayout = localWavefrontDescriptorSetLayout;
        wavefrontBootstrapDescriptorSet = localWavefrontBootstrapDescriptorSet;
        wavefrontForwardDescriptorSet = localWavefrontForwardDescriptorSet;
        wavefrontReverseDescriptorSet = localWavefrontReverseDescriptorSet;
        accelDescriptorSetLayout = localAccelDescriptorSetLayout;
        accelDescriptorSet = localAccelDescriptorSet;
        pipelineLayout = pipelineLayoutLocal;

        var initCommandBuffer = Context.CreateCommandBuffer();
        Context.BeginCommandBuffer(initCommandBuffer);
        UpdateSceneResources(initCommandBuffer, force: true);
        Context.SubmitAndWait(initCommandBuffer);
        Log.Information("RTX wavefront raytracer created.");
    }

    protected override void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force)
    {
        var revisions = Scene.GetResourceRevisions();
        var meshesDirty = force || uploadedMeshesRevision != revisions.Meshes;
        var tlasDirty = force || uploadedTlasRevision != revisions.Tlas || uploadedMeshesRevision != revisions.Meshes;
        if (!meshesDirty && !tlasDirty)
            return;

        if (meshesDirty)
        {
            UpdateMeshSceneBuffers(commandBuffer, ref instancesBuffer, ref meshBuffer, descriptorSet);
            uploadedMeshesRevision = revisions.Meshes;
        }

        if (tlasDirty)
        {
            var nextTlasSlotIndex = force ? activeTlasSlotIndex : (activeTlasSlotIndex + 1) % tlasSlots.Length;
            UpdateTlas(commandBuffer, tlasSlots[nextTlasSlotIndex], nextTlasSlotIndex);
            activeTlasSlotIndex = nextTlasSlotIndex;
            uploadedTlasRevision = revisions.Tlas;
        }
    }

    protected override void ExecuteRaytracing(Context.CommandBuffer commandBuffer, VulkanImage image, PushDataGpu pushConstants)
    {
        var boundSets = stackalloc DescriptorSet[3];
        boundSets[0] = descriptorSet;
        boundSets[1] = wavefrontBootstrapDescriptorSet;
        boundSets[2] = accelDescriptorSet;

        ExecuteWavefrontPass(
            commandBuffer,
            image,
            pushConstants,
            pipelineLayout,
            wavefrontBootstrapDescriptorSet,
            wavefrontForwardDescriptorSet,
            wavefrontReverseDescriptorSet,
            3,
            boundSets,
            GetPipelineBundle(),
            "RTX");
    }

    protected override DescriptorSet GetDescriptorSet() => descriptorSet;
    protected override DescriptorSetLayout GetDescriptorSetLayout() => descriptorSetLayout;

    private void CreateAccelDescriptorSet(out DescriptorSetLayout layout, out DescriptorSet set)
    {
        layout = DeviceResources.Track(new VulkanDescriptorSetBuilder()
            .Add(0, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit)
            .BuildLayout(Context));
        set = VulkanDescriptorSet.Allocate(Context, layout);
    }

    private PipelineBundle GetPipelineBundle()
    {
        return EffectiveRenderMode switch
        {
            RenderMode.AmbientOcclusion => aoPipelines,
            RenderMode.PathTracing => pathTracingPipelines,
            _ => pathTracingPipelines
        };
    }

    private void UpdateTlas(Context.CommandBuffer commandBuffer, TlasResourceSlot slot, int slotIndex)
    {
        var instanceBytes = Scene.BuildRtxInstanceData();
        var previousInstancesBuffer = slot.InstancesBuffer;
        slot.InstancesBuffer = UploadDeviceLocalBuffer(
            commandBuffer,
            instanceBytes,
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit);
        Context.RetainForExecution(commandBuffer, previousInstancesBuffer);
        InsertTransferToAccelerationStructureReadBarrier(commandBuffer.InternalHandle);

        var primitiveCount = (uint)Math.Max(1, instanceBytes.Length / Marshal.SizeOf<AccelerationStructureInstanceKHR>());
        slot.Tlas.BuildTopLevel(commandBuffer, primitiveCount, slot.InstancesBuffer.DeviceAddress);

        VulkanDescriptorWriter.UpdateAccelerationStructure(Context, accelDescriptorSet, 0, slot.Tlas.Handle);
        Log.Debug(
            "RTX TLAS updated: slot={Slot} handle=0x{Handle:X} instancesBytes={InstanceBytes}",
            slotIndex,
            slot.Tlas.Handle.Handle,
            instanceBytes.Length);
    }

    public override void Dispose()
    {
        DisposeCommonResources();
        DisposeWavefrontResources();
        meshBuffer.Dispose();
        instancesBuffer.Dispose();
        foreach (var slot in tlasSlots)
            slot.Dispose();

        DeviceResources.Dispose();
    }
}
