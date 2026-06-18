using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Rasterizing;

internal sealed unsafe class LightProbeSystem : IDisposable
{
    private const int ProbesPerDim = 8;
    private const int ProbeStride = 8;
    private const int BakeSamples = 64;
    private const int GridDimX = ProbesPerDim;
    private const int GridDimY = ProbesPerDim;
    private const int GridDimZ = ProbesPerDim;
    private const int TotalProbes = GridDimX * GridDimY * GridDimZ;

    private readonly Context context;
    private readonly PipelineLayout bakePipelineLayout;
    private readonly Pipeline bakePipelineRT;
    private readonly Pipeline bakePipelineCompute;
    private readonly DescriptorSetLayout bakeDescriptorSetLayout;
    private readonly DescriptorPool bakeDescriptorPool;

    public VulkanBuffer ProbeBuffer { get; private set; }
    public int ProbeCount { get; private set; }
    public Vector3 GridMin { get; private set; }
    public Vector3 GridMax { get; private set; }
    public Vector3 GridCellSize { get; private set; }

    public LightProbeSystem(Context context)
    {
        this.context = context;

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<BakePushConstants>()
        };

        bakeDescriptorSetLayout = CreateBakeDescriptorSetLayout();
        Span<DescriptorSetLayout> layouts = stackalloc DescriptorSetLayout[1];
        layouts[0] = bakeDescriptorSetLayout;
        bakePipelineLayout = VulkanPipelineFactory.CreatePipelineLayout(context, layouts, pushRange);

        bakePipelineRT = context.RayTracingSupported
            ? VulkanPipelineFactory.CreateComputePipeline(
                context, bakePipelineLayout, "Assets/Shaders/Raster/LightProbeBakeRtx.spv")
            : default;
        bakePipelineCompute = VulkanPipelineFactory.CreateComputePipeline(
            context, bakePipelineLayout, "Assets/Shaders/Raster/LightProbeBakeCompute.spv");

        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 2);
        poolSizes[1] = new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, 1);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
        };
        context.Api.CreateDescriptorPool(context.Device, in poolInfo, null, out bakeDescriptorPool).ThrowOnError();

        var initData = new float[TotalProbes * ProbeStride * 4];
        for (int i = 0; i < TotalProbes; i++)
        {
            initData[i * ProbeStride * 4] = float.NaN;
        }
        ProbeBuffer = new VulkanBuffer(
            context,
            (ulong)(TotalProbes * ProbeStride * 16),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            MemoryMarshal.AsBytes(new ReadOnlySpan<float>(initData)));
        ProbeCount = 0;
    }

    public void PlaceGrid(Vector3 sceneMin, Vector3 sceneMax)
    {
        GridMin = sceneMin;
        GridMax = sceneMax;
        GridCellSize = (sceneMax - sceneMin) / new Vector3(GridDimX, GridDimY, GridDimZ);

        // Upload probe positions
        var positions = new float[TotalProbes * ProbeStride * 4];
        for (int iz = 0; iz < GridDimZ; iz++)
        {
            for (int iy = 0; iy < GridDimY; iy++)
            {
                for (int ix = 0; ix < GridDimX; ix++)
                {
                    int idx = (iz * GridDimY * GridDimX + iy * GridDimX + ix) * ProbeStride * 4;
                    float tX = (ix + 0.5f) / GridDimX;
                    float tY = (iy + 0.5f) / GridDimY;
                    float tZ = (iz + 0.5f) / GridDimZ;
                    positions[idx + 0] = Vector3.Lerp(sceneMin, sceneMax, new Vector3(tX, tY, tZ)).X;
                    positions[idx + 1] = Vector3.Lerp(sceneMin, sceneMax, new Vector3(tX, tY, tZ)).Y;
                    positions[idx + 2] = Vector3.Lerp(sceneMin, sceneMax, new Vector3(tX, tY, tZ)).Z;
                    positions[idx + 3] = 0f;
                }
            }
        }
        ProbeBuffer.Upload(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(positions)));
        ProbeCount = TotalProbes;
    }

    public void Bake(Context.CommandBuffer cmd, Scene scene, bool useRT)
    {
        if (ProbeCount == 0) return;
        if (useRT)
            throw new NotSupportedException("RTX light probe baking is not wired to the raster TLAS descriptor set yet.");

        var descriptorSet = AllocateBakeDescriptorSet();

        var writer = new VulkanDescriptorWriter()
            .StorageBuffer(16, ProbeBuffer);
        if (useRT)
        {
            // For RT path, bind TLAS at 17
            // TODO: TLAS descriptor binding
        }
        writer.Update(context, descriptorSet);

        var pipeline = bakePipelineCompute;
        context.Api.CmdBindPipeline(cmd.InternalHandle, PipelineBindPoint.Compute, pipeline);
        context.Api.CmdBindDescriptorSets(cmd.InternalHandle, PipelineBindPoint.Compute, bakePipelineLayout, 0, 1, in descriptorSet, 0, null);

        var push = new BakePushConstants
        {
            ProbeBaseIndex = 0,
            TotalProbes = (uint)ProbeCount,
            GridDimX = GridDimX,
            GridDimY = GridDimY
        };
        context.Api.CmdPushConstants(cmd.InternalHandle, bakePipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)Marshal.SizeOf<BakePushConstants>(), &push);
        context.Api.CmdDispatch(cmd.InternalHandle, (uint)ProbeCount, 1, 1);
        VulkanBarriers.Memory(
            context.Api,
            cmd.InternalHandle,
            AccessFlags.ShaderWriteBit,
            AccessFlags.ShaderReadBit,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit);

        context.Api.FreeDescriptorSets(context.Device, bakeDescriptorPool, 1, in descriptorSet);
    }

    private DescriptorSetLayout CreateBakeDescriptorSetLayout()
    {
        return new VulkanDescriptorSetBuilder()
            .Add(16, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit)
            .Add(17, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit | ShaderStageFlags.RaygenBitKhr)
            .BuildLayout(context);
    }

    private DescriptorSet AllocateBakeDescriptorSet()
    {
        var layout = bakeDescriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = bakeDescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        context.Api.AllocateDescriptorSets(context.Device, in allocInfo, out var set).ThrowOnError();
        return set;
    }

    public void Dispose()
    {
        ProbeBuffer.Dispose();
        if (bakePipelineRT.Handle != 0)
            context.Api.DestroyPipeline(context.Device, bakePipelineRT, null);
        context.Api.DestroyPipeline(context.Device, bakePipelineCompute, null);
        context.Api.DestroyPipelineLayout(context.Device, bakePipelineLayout, null);
        context.Api.DestroyDescriptorSetLayout(context.Device, bakeDescriptorSetLayout, null);
        context.Api.DestroyDescriptorPool(context.Device, bakeDescriptorPool, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BakePushConstants
    {
        public uint ProbeBaseIndex;
        public uint TotalProbes;
        public uint GridDimX;
        public uint GridDimY;
    }
}
