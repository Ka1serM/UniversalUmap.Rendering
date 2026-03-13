using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private const int MaxTlasBuildsPerSecond = 10;
    private static readonly TimeSpan MinTlasRebuildInterval = TimeSpan.FromSeconds(1d / MaxTlasBuildsPerSecond);
    private readonly KhrAccelerationStructure accelExt;
    private readonly KhrRayTracingPipeline rtExt;

    private readonly DescriptorPool descriptorPool;
    private readonly DescriptorSetLayout descriptorSetLayout;
    private readonly DescriptorSet descriptorSet;
    private readonly PipelineLayout pipelineLayout;
    private readonly RayTracingPipelineState fullPathPipelineState;
    private readonly RayTracingPipelineState aoPipelineState;

    private readonly TlasResourceSlot[] tlasSlots;
    private int activeTlasSlotIndex;
    private int queuedTlasSlotIndex = -1;
    private long lastTlasBuildTicks;
    private GpuBuffer meshBuffer = default!;

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
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                new byte[16]);
        }

        public void Dispose()
        {
            InstancesBuffer.Dispose();
            Tlas.Dispose();
        }
    }

    private sealed class RayTracingPipelineState : IDisposable
    {
        public Pipeline Pipeline;
        public GpuBuffer RaygenSbt = default!;
        public GpuBuffer MissSbt = default!;
        public GpuBuffer HitSbt = default!;
        public StridedDeviceAddressRegionKHR RaygenRegion;
        public StridedDeviceAddressRegionKHR MissRegion;
        public StridedDeviceAddressRegionKHR HitRegion;
        public StridedDeviceAddressRegionKHR CallableRegion;

        public void Dispose()
        {
            HitSbt.Dispose();
            MissSbt.Dispose();
            RaygenSbt.Dispose();
        }
    }

    public RtxRaytracer(Context context, Scene scene)
        : base(context, scene)
    {
        if (!Context.Api.TryGetDeviceExtension(Context.Instance, Context.Device, out accelExt))
            throw new InvalidOperationException("VK_KHR_acceleration_structure extension is not available.");
        if (!Context.Api.TryGetDeviceExtension(Context.Instance, Context.Device, out rtExt))
            throw new InvalidOperationException("VK_KHR_ray_tracing_pipeline extension is not available.");

        tlasSlots = new TlasResourceSlot[TlasBufferCount];
        for (var i = 0; i < tlasSlots.Length; i++)
            tlasSlots[i] = new TlasResourceSlot(Context, accelExt);
        activeTlasSlotIndex = 0;
        lastTlasBuildTicks = 0;
        meshBuffer = new GpuBuffer(
            Context,
            16,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            new byte[16]);

        var fullPathRaygenBytes = EmbeddedAssets.ReadByFileName("RayGeneration.spv");
        var aoRaygenBytes = EmbeddedAssets.ReadByFileName("RayGenerationAo.spv");
        var missBytes = EmbeddedAssets.ReadByFileName("Miss.spv");
        var aoMissBytes = EmbeddedAssets.ReadByFileName("MissAo.spv");
        var shadowMissBytes = EmbeddedAssets.ReadByFileName("ShadowMiss.spv");
        var hitBytes = EmbeddedAssets.ReadByFileName("ClosestHit.spv");
        var aoHitBytes = EmbeddedAssets.ReadByFileName("ClosestHitAo.spv");
        using var mainName = new ByteString("main");

        var fullPathRaygenModule = CreateShaderModule(fullPathRaygenBytes);
        var aoRaygenModule = CreateShaderModule(aoRaygenBytes);
        var missModule = CreateShaderModule(missBytes);
        var aoMissModule = CreateShaderModule(aoMissBytes);
        var shadowMissModule = CreateShaderModule(shadowMissBytes);
        var hitModule = CreateShaderModule(hitBytes);
        var aoHitModule = CreateShaderModule(aoHitBytes);

        var layoutBindings = stackalloc DescriptorSetLayoutBinding[9];
        layoutBindings[0] = new DescriptorSetLayoutBinding(
            0,
            DescriptorType.AccelerationStructureKhr,
            1,
            ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.ClosestHitBitKhr);
        layoutBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageImage, 1, ShaderStageFlags.RaygenBitKhr);
        layoutBindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.StorageImage, 1, ShaderStageFlags.RaygenBitKhr);
        layoutBindings[3] = new DescriptorSetLayoutBinding(3, DescriptorType.StorageImage, 1, ShaderStageFlags.RaygenBitKhr);
        layoutBindings[4] = new DescriptorSetLayoutBinding(4, DescriptorType.StorageImage, 1, ShaderStageFlags.RaygenBitKhr);
        layoutBindings[5] = new DescriptorSetLayoutBinding(5, DescriptorType.StorageImage, 1, ShaderStageFlags.RaygenBitKhr);
        layoutBindings[6] = new DescriptorSetLayoutBinding(6, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ClosestHitBitKhr);
        layoutBindings[7] = new DescriptorSetLayoutBinding(
            7,
            DescriptorType.StorageBuffer,
            1,
            ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.MissBitKhr);
        layoutBindings[8] = new DescriptorSetLayoutBinding(
            8,
            DescriptorType.CombinedImageSampler,
            MaxTextures,
            ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.MissBitKhr | ShaderStageFlags.FragmentBit);
        var bindingFlags = stackalloc DescriptorBindingFlags[9];
        bindingFlags[8] = DescriptorBindingFlags.PartiallyBoundBit |
                          DescriptorBindingFlags.VariableDescriptorCountBit |
                          DescriptorBindingFlags.UpdateAfterBindBit;

        var bindingFlagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 9,
            PBindingFlags = bindingFlags
        };

        var descriptorSetLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 9,
            PBindings = layoutBindings,
            PNext = &bindingFlagsInfo
        };
        Context.Api.CreateDescriptorSetLayout(Context.Device, in descriptorSetLayoutInfo, default, out var descriptorSetLayoutLocal).ThrowOnError();

        var poolSizes = stackalloc DescriptorPoolSize[4];
        poolSizes[0] = new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, 1);
        poolSizes[1] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 2);
        poolSizes[2] = new DescriptorPoolSize(DescriptorType.StorageImage, 5);
        poolSizes[3] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, MaxTextures);
        var descriptorPoolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 4,
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
            StageFlags = ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.MissBitKhr,
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

        var fullPathPipeline = CreateRayTracingPipeline(fullPathRaygenModule, missModule, shadowMissModule, hitModule, pipelineLayoutLocal, mainName);
        var aoPipeline = CreateRayTracingPipeline(aoRaygenModule, aoMissModule, shadowMissModule, aoHitModule, pipelineLayoutLocal, mainName);

        Context.Api.DestroyShaderModule(Context.Device, aoHitModule, default);
        Context.Api.DestroyShaderModule(Context.Device, hitModule, default);
        Context.Api.DestroyShaderModule(Context.Device, shadowMissModule, default);
        Context.Api.DestroyShaderModule(Context.Device, aoMissModule, default);
        Context.Api.DestroyShaderModule(Context.Device, missModule, default);
        Context.Api.DestroyShaderModule(Context.Device, aoRaygenModule, default);
        Context.Api.DestroyShaderModule(Context.Device, fullPathRaygenModule, default);

        descriptorSetLayout = descriptorSetLayoutLocal;
        descriptorPool = descriptorPoolLocal;
        descriptorSet = descriptorSetLocal;
        pipelineLayout = pipelineLayoutLocal;
        fullPathPipelineState = BuildShaderBindingTable(fullPathPipeline);
        aoPipelineState = BuildShaderBindingTable(aoPipeline);
        Log.Information("RTX raytracer pipelines and descriptors created.");
        var initCommandBuffer = Context.CreateCommandBuffer();
        Context.BeginCommandBuffer(initCommandBuffer);
        UpdateSceneResources(initCommandBuffer, force: true);
        Context.SubmitAndWait(initCommandBuffer);
    }

    protected override void ExecuteRaytracing(CommandBuffer commandBuffer, ImageResource image, PushDataGpu pushConstants)
    {
        var isAoMode = Scene.RenderMode != RenderMode.PathTracing;
        var pipelineState = isAoMode ? aoPipelineState : fullPathPipelineState;

        Context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.RayTracingKhr, pipelineState.Pipeline);
        Context.Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.RayTracingKhr, pipelineLayout, 0, 1, in descriptorSet, 0, null);

        Context.Api.CmdPushConstants(
            commandBuffer,
            pipelineLayout,
            ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.MissBitKhr,
            0,
            (uint)sizeof(PushDataGpu),
            &pushConstants);

        var width = (uint)Math.Max(1, image.Size.Width);
        var height = (uint)Math.Max(1, image.Size.Height);
        rtExt.CmdTraceRays(
            commandBuffer,
            in pipelineState.RaygenRegion,
            in pipelineState.MissRegion,
            in pipelineState.HitRegion,
            in pipelineState.CallableRegion,
            width,
            height,
            1);
        if (pushConstants.Frame < 3)
            Log.Debug("RTX trace frame={Frame}: rays={Width}x{Height}.", pushConstants.Frame, width, height);
    }

    protected override void UpdateSceneResources(Context.CommandBuffer commandBuffer, bool force)
    {
        var meshesDirty = force || Scene.IsDirty(SceneDirtyFlags.Meshes);
        var tlasDirty = force || Scene.IsDirty(SceneDirtyFlags.Tlas | SceneDirtyFlags.Meshes);
        if (!meshesDirty && !tlasDirty && queuedTlasSlotIndex < 0)
            return;

        var meshBytesLength = 0;
        if (meshesDirty)
        {
            var meshBytes = Scene.BuildMeshAddressData();
            meshBytesLength = meshBytes.Length;

            var previousMeshBuffer = meshBuffer;
            meshBuffer = new GpuBuffer(
                Context,
                (ulong)meshBytes.Length,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                meshBytes);
            Context.RetainForExecution(commandBuffer, previousMeshBuffer);

            var meshInfo = new DescriptorBufferInfo(meshBuffer.Handle, 0, meshBuffer.Size);
            var meshWrite = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = 6,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                PBufferInfo = &meshInfo
            };
            Context.Api.UpdateDescriptorSets(Context.Device, 1, in meshWrite, 0, null);
            Scene.ClearDirty(SceneDirtyFlags.Meshes);
        }

        if (tlasDirty)
            queuedTlasSlotIndex = force ? activeTlasSlotIndex : (activeTlasSlotIndex + 1) % tlasSlots.Length;

        if (queuedTlasSlotIndex >= 0 && ShouldBuildTlasNow(force))
        {
            var tlasSlot = tlasSlots[queuedTlasSlotIndex];
            UpdateTlas(commandBuffer, tlasSlot, queuedTlasSlotIndex);
            activeTlasSlotIndex = queuedTlasSlotIndex;
            queuedTlasSlotIndex = -1;
            lastTlasBuildTicks = Stopwatch.GetTimestamp();
            Scene.ClearDirty(SceneDirtyFlags.Tlas);
        }

        if (meshesDirty || tlasDirty)
        {
            Log.Debug(
                "RTX scene resources updated: meshBytes={MeshBytes}, tlasSlot={TlasSlot}/{SlotCount}, tlasQueued={TlasQueued}.",
                meshBytesLength,
                activeTlasSlotIndex,
                tlasSlots.Length,
                queuedTlasSlotIndex >= 0);
        }
    }

    protected override DescriptorSet GetDescriptorSet() => descriptorSet;
    protected override DescriptorSetLayout GetDescriptorSetLayout() => descriptorSetLayout;

    private bool ShouldBuildTlasNow(bool force)
    {
        if (force || lastTlasBuildTicks == 0)
            return true;

        var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - lastTlasBuildTicks) / (double)Stopwatch.Frequency);
        return elapsed >= MinTlasRebuildInterval;
    }

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

    private Pipeline CreateRayTracingPipeline(
        ShaderModule raygenModule,
        ShaderModule missModule,
        ShaderModule shadowMissModule,
        ShaderModule hitModule,
        PipelineLayout layout,
        ByteString mainName)
    {
        var stages = stackalloc PipelineShaderStageCreateInfo[4];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.RaygenBitKhr,
            Module = raygenModule,
            PName = mainName
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.MissBitKhr,
            Module = missModule,
            PName = mainName
        };
        stages[2] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.MissBitKhr,
            Module = shadowMissModule,
            PName = mainName
        };
        stages[3] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ClosestHitBitKhr,
            Module = hitModule,
            PName = mainName
        };

        var groups = stackalloc RayTracingShaderGroupCreateInfoKHR[4];
        const uint shaderUnused = 0xFFFFFFFF;
        groups[0] = new RayTracingShaderGroupCreateInfoKHR
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
            GeneralShader = 0,
            ClosestHitShader = shaderUnused,
            AnyHitShader = shaderUnused,
            IntersectionShader = shaderUnused
        };
        groups[1] = new RayTracingShaderGroupCreateInfoKHR
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
            GeneralShader = 1,
            ClosestHitShader = shaderUnused,
            AnyHitShader = shaderUnused,
            IntersectionShader = shaderUnused
        };
        groups[2] = new RayTracingShaderGroupCreateInfoKHR
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
            GeneralShader = 2,
            ClosestHitShader = shaderUnused,
            AnyHitShader = shaderUnused,
            IntersectionShader = shaderUnused
        };
        groups[3] = new RayTracingShaderGroupCreateInfoKHR
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.TrianglesHitGroupKhr,
            GeneralShader = shaderUnused,
            ClosestHitShader = 3,
            AnyHitShader = shaderUnused,
            IntersectionShader = shaderUnused
        };

        var rtPipelineInfo = new RayTracingPipelineCreateInfoKHR
        {
            SType = StructureType.RayTracingPipelineCreateInfoKhr,
            StageCount = 4,
            PStages = stages,
            GroupCount = 4,
            PGroups = groups,
            // Primary ray from raygen + one shadow ray from closest-hit.
            MaxPipelineRayRecursionDepth = 2,
            Layout = layout
        };
        rtExt.CreateRayTracingPipelines(Context.Device, default, default, 1, in rtPipelineInfo, default, out var pipeline).ThrowOnError();
        return pipeline;
    }

    private RayTracingPipelineState BuildShaderBindingTable(Pipeline pipeline)
    {
        var rtProps = new PhysicalDeviceRayTracingPipelinePropertiesKHR
        {
            SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr
        };
        var props2 = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &rtProps
        };
        Context.Api.GetPhysicalDeviceProperties2(Context.PhysicalDevice, &props2);

        var handleSize = rtProps.ShaderGroupHandleSize;
        var handleAlignment = rtProps.ShaderGroupHandleAlignment;
        var handleSizeAligned = (handleSize + handleAlignment - 1) & ~(handleAlignment - 1);
        var groupCount = 4u;
        var handleStorageSize = (int)(groupCount * handleSizeAligned);
        var handleStorage = new byte[handleStorageSize];

        fixed (byte* pHandles = handleStorage)
        {
            rtExt.GetRayTracingShaderGroupHandles(Context.Device, pipeline, 0, groupCount, (nuint)handleStorageSize, pHandles).ThrowOnError();
        }

        var raygenSize = handleSizeAligned;
        var missSize = handleSizeAligned * 2;
        var hitSize = handleSizeAligned;

        var state = new RayTracingPipelineState
        {
            Pipeline = pipeline
        };

        state.RaygenSbt = new GpuBuffer(
            Context,
            raygenSize,
            BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            handleStorage.AsSpan(0, (int)raygenSize));
        state.MissSbt = new GpuBuffer(
            Context,
            missSize,
            BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            handleStorage.AsSpan((int)raygenSize, (int)missSize));
        state.HitSbt = new GpuBuffer(
            Context,
            hitSize,
            BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            handleStorage.AsSpan((int)(raygenSize + missSize), (int)hitSize));

        state.RaygenRegion = new StridedDeviceAddressRegionKHR(state.RaygenSbt.DeviceAddress, handleSizeAligned, raygenSize);
        state.MissRegion = new StridedDeviceAddressRegionKHR(state.MissSbt.DeviceAddress, handleSizeAligned, missSize);
        state.HitRegion = new StridedDeviceAddressRegionKHR(state.HitSbt.DeviceAddress, handleSizeAligned, hitSize);
        state.CallableRegion = default;
        Log.Information("RTX SBT built (handleSizeAligned={HandleSizeAligned}, groups={GroupCount}).", handleSizeAligned, groupCount);
        return state;
    }

    private void UpdateTlas(Context.CommandBuffer commandBuffer, TlasResourceSlot slot, int slotIndex)
    {
        var instanceBytes = Scene.BuildRtxInstanceData();
        var previousInstancesBuffer = slot.InstancesBuffer;
        slot.InstancesBuffer = new GpuBuffer(
            Context,
            (ulong)instanceBytes.Length,
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            instanceBytes);
        Context.RetainForExecution(commandBuffer, previousInstancesBuffer);

        var primitiveCount = (uint)Math.Max(1, instanceBytes.Length / Marshal.SizeOf<AccelerationStructureInstanceKHR>());
        // NoorRay-style: TLAS upload/build via one-time immediate submit path.
        // Frame command buffer stays focused on ray trace/composite work.
        slot.Tlas.BuildTopLevel(primitiveCount, slot.InstancesBuffer.DeviceAddress);

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
            DstSet = descriptorSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            PNext = &accelInfo
        };
        Context.Api.UpdateDescriptorSets(Context.Device, 1, in accelWrite, 0, null);
        Log.Debug(
            "TLAS updated: instanceBytes={InstanceBytes}, primitiveCount={PrimitiveCount}, sceneSlot={SceneSlot}/{SlotCount}.",
            instanceBytes.Length,
            primitiveCount,
            slotIndex,
            tlasSlots.Length);
    }

    public override void Dispose()
    {
        DisposeCommonResources();
        aoPipelineState.Dispose();
        fullPathPipelineState.Dispose();
        meshBuffer.Dispose();
        foreach (var slot in tlasSlots)
            slot.Dispose();

        if (aoPipelineState.Pipeline.Handle != default)
            Context.Api.DestroyPipeline(Context.Device, aoPipelineState.Pipeline, default);
        if (fullPathPipelineState.Pipeline.Handle != default)
            Context.Api.DestroyPipeline(Context.Device, fullPathPipelineState.Pipeline, default);
        if (pipelineLayout.Handle != default)
            Context.Api.DestroyPipelineLayout(Context.Device, pipelineLayout, default);
        if (descriptorSetLayout.Handle != default)
            Context.Api.DestroyDescriptorSetLayout(Context.Device, descriptorSetLayout, default);
        if (descriptorPool.Handle != default)
            Context.Api.DestroyDescriptorPool(Context.Device, descriptorPool, default);
    }
}
