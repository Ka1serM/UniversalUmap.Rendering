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
        public GpuBuffer InstancesBuffer;

        public TlasResourceSlot(Context context, KhrAccelerationStructure accelExt)
        {
            Tlas = new Accel(context, accelExt);
            InstancesBuffer = new GpuBuffer(
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
    private readonly PipelineBundle directLightingPipelines;
    private readonly PipelineBundle pathTracingPipelines;
    private readonly TlasResourceSlot[] tlasSlots;

    private int activeTlasSlotIndex;
    private GpuBuffer instancesBuffer;
    private GpuBuffer meshBuffer;

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

        var setLayouts = stackalloc DescriptorSetLayout[3];
        setLayouts[0] = primaryDescriptorSetLayout;
        setLayouts[1] = localWavefrontDescriptorSetLayout;
        setLayouts[2] = localAccelDescriptorSetLayout;

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)sizeof(PushDataGpu)
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };
        Context.Api.CreatePipelineLayout(Context.Device, in pipelineLayoutInfo, default, out var pipelineLayoutLocal).ThrowOnError();

        aoPipelines = CreatePipelineBundle("Ao", "Rtx", pipelineLayoutLocal, mainName);
        directLightingPipelines = CreatePipelineBundle("Direct", "Rtx", pipelineLayoutLocal, mainName);
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
        var meshesDirty = force || Scene.IsDirty(SceneDirtyFlags.Meshes);
        var tlasDirty = force || Scene.IsDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Meshes);
        if (!meshesDirty && !tlasDirty)
            return;

        if (meshesDirty)
        {
            UpdateMeshSceneBuffers(commandBuffer, ref instancesBuffer, ref meshBuffer, descriptorSet);
            Scene.ClearDirty(SceneDirtyFlags.Meshes);
        }

        if (tlasDirty)
        {
            var nextTlasSlotIndex = force ? activeTlasSlotIndex : (activeTlasSlotIndex + 1) % tlasSlots.Length;
            UpdateTlas(commandBuffer, tlasSlots[nextTlasSlotIndex], nextTlasSlotIndex);
            activeTlasSlotIndex = nextTlasSlotIndex;
            Scene.ClearDirty(SceneDirtyFlags.Tlas);
        }
    }

    protected override void ExecuteRaytracing(Context.CommandBuffer commandBuffer, ImageResource image, PushDataGpu pushConstants)
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
        var binding = new DescriptorSetLayoutBinding(0, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit);
        var descriptorSetLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        Context.Api.CreateDescriptorSetLayout(Context.Device, in descriptorSetLayoutInfo, default, out layout).ThrowOnError();

        var layoutLocal = layout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = Context.DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layoutLocal
        };
        Context.Api.AllocateDescriptorSets(Context.Device, in allocInfo, out set).ThrowOnError();
    }

    private PipelineBundle GetPipelineBundle()
    {
        return CachedRenderMode switch
        {
            RenderMode.AmbientOcclusion => aoPipelines,
            RenderMode.DirectLighting => directLightingPipelines,
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

        var accelInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1
        };
        var tlasHandle = slot.Tlas.Handle;
        accelInfo.PAccelerationStructures = &tlasHandle;

        var accelWrite = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = accelDescriptorSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            PNext = &accelInfo
        };
        Context.Api.UpdateDescriptorSets(Context.Device, 1, in accelWrite, 0, null);
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

        DestroyPipelineBundle(pathTracingPipelines);
        DestroyPipelineBundle(directLightingPipelines);
        DestroyPipelineBundle(aoPipelines);
        if (pipelineLayout.Handle != default)
            Context.Api.DestroyPipelineLayout(Context.Device, pipelineLayout, default);
        if (accelDescriptorSetLayout.Handle != default)
            Context.Api.DestroyDescriptorSetLayout(Context.Device, accelDescriptorSetLayout, default);
        if (wavefrontDescriptorSetLayout.Handle != default)
            Context.Api.DestroyDescriptorSetLayout(Context.Device, wavefrontDescriptorSetLayout, default);
        if (descriptorSetLayout.Handle != default)
            Context.Api.DestroyDescriptorSetLayout(Context.Device, descriptorSetLayout, default);
    }
}
