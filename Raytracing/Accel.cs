using System;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Raytracing;

internal sealed unsafe class Accel : IDisposable
{
    private readonly Context context;
    private readonly KhrAccelerationStructure ext;
    private VulkanBuffer? storageBuffer;

    public AccelerationStructureKHR Handle { get; private set; }
    public AccelerationStructureTypeKHR Type { get; private set; }

    private sealed class DeferredAccelResources : IDisposable
    {
        private readonly Context context;
        private readonly KhrAccelerationStructure ext;
        private readonly AccelerationStructureKHR handle;
        private readonly VulkanBuffer? storage;

        public DeferredAccelResources(Context context, KhrAccelerationStructure ext, AccelerationStructureKHR handle, VulkanBuffer? storage)
        {
            this.context = context;
            this.ext = ext;
            this.handle = handle;
            this.storage = storage;
        }

        public void Dispose()
        {
            if (handle.Handle != default)
                ext.DestroyAccelerationStructure(context.Device, handle, default);
            storage?.Dispose();
        }
    }

    public Accel(Context context, KhrAccelerationStructure ext)
    {
        this.context = context;
        this.ext = ext;
    }

    public void BuildTopLevel(uint primitiveCount, ulong instancesDeviceAddress)
    {
        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);
        BuildTopLevel(commandBuffer, primitiveCount, instancesDeviceAddress);
        context.SubmitAndWait(commandBuffer);
    }

    public void BuildTopLevel(Context.CommandBuffer commandBuffer, uint primitiveCount, ulong instancesDeviceAddress)
    {
        var previousHandle = Handle;
        var previousStorage = storageBuffer;

        var instancesData = new AccelerationStructureGeometryInstancesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
            ArrayOfPointers = false,
            Data = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = instancesDeviceAddress
            }
        };
        var geometryData = new AccelerationStructureGeometryDataKHR
        {
            Instances = instancesData
        };
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = geometryData,
            Flags = GeometryFlagsKHR.OpaqueBitKhr
        };

        var buildInfo = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1
        };
        buildInfo.PGeometries = &geometry;

        var sizeInfo = new AccelerationStructureBuildSizesInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildSizesInfoKhr
        };
        ext.GetAccelerationStructureBuildSizes(
            context.Device,
            AccelerationStructureBuildTypeKHR.DeviceKhr,
            &buildInfo,
            &primitiveCount,
            &sizeInfo);

        storageBuffer = new VulkanBuffer(
            context,
            sizeInfo.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit);

        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = storageBuffer.Handle,
            Size = sizeInfo.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.TopLevelKhr
        };
        ext.CreateAccelerationStructure(context.Device, in createInfo, default, out var handle).ThrowOnError();
        Handle = handle;
        Type = AccelerationStructureTypeKHR.TopLevelKhr;

        if (previousHandle.Handle != default || previousStorage is not null)
            context.RetainForExecution(commandBuffer, new DeferredAccelResources(context, ext, previousHandle, previousStorage));

        var scratch = new VulkanBuffer(
            context,
            sizeInfo.BuildScratchSize,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit);

        buildInfo.DstAccelerationStructure = handle;
        buildInfo.ScratchData = new DeviceOrHostAddressKHR
        {
            DeviceAddress = scratch.DeviceAddress
        };

        var rangeInfo = new AccelerationStructureBuildRangeInfoKHR
        {
            PrimitiveCount = primitiveCount,
            PrimitiveOffset = 0,
            FirstVertex = 0,
            TransformOffset = 0
        };
        var pRangeInfo = &rangeInfo;

        ext.CmdBuildAccelerationStructures(commandBuffer.InternalHandle, 1, in buildInfo, &pRangeInfo);
        context.RetainForExecution(commandBuffer, scratch);
        InsertBuildToReadBarrier(commandBuffer.InternalHandle);
    }

    public void BuildBottomLevelTriangles(
        uint primitiveCount,
        ulong vertexAddress,
        ulong vertexStride,
        uint maxVertex,
        ulong indexAddress)
    {
        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);
        BuildBottomLevelTriangles(commandBuffer, primitiveCount, vertexAddress, vertexStride, maxVertex, indexAddress);
        context.SubmitAndWait(commandBuffer);
    }

    public void BuildBottomLevelTriangles(
        Context.CommandBuffer commandBuffer,
        uint primitiveCount,
        ulong vertexAddress,
        ulong vertexStride,
        uint maxVertex,
        ulong indexAddress)
    {
        var previousHandle = Handle;
        var previousStorage = storageBuffer;

        var trianglesData = new AccelerationStructureGeometryTrianglesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
            VertexFormat = Format.R32G32B32Sfloat,
            VertexData = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = vertexAddress
            },
            VertexStride = vertexStride,
            MaxVertex = maxVertex,
            IndexType = IndexType.Uint32,
            IndexData = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = indexAddress
            }
        };
        var geometryData = new AccelerationStructureGeometryDataKHR
        {
            Triangles = trianglesData
        };
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,
            Geometry = geometryData,
            Flags = GeometryFlagsKHR.OpaqueBitKhr
        };

        var buildInfo = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1
        };
        buildInfo.PGeometries = &geometry;

        var sizeInfo = new AccelerationStructureBuildSizesInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildSizesInfoKhr
        };
        ext.GetAccelerationStructureBuildSizes(
            context.Device,
            AccelerationStructureBuildTypeKHR.DeviceKhr,
            &buildInfo,
            &primitiveCount,
            &sizeInfo);

        storageBuffer = new VulkanBuffer(
            context,
            sizeInfo.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit);

        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = storageBuffer.Handle,
            Size = sizeInfo.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr
        };
        ext.CreateAccelerationStructure(context.Device, in createInfo, default, out var handle).ThrowOnError();
        Handle = handle;
        Type = AccelerationStructureTypeKHR.BottomLevelKhr;

        if (previousHandle.Handle != default || previousStorage is not null)
            context.RetainForExecution(commandBuffer, new DeferredAccelResources(context, ext, previousHandle, previousStorage));

        var scratch = new VulkanBuffer(
            context,
            sizeInfo.BuildScratchSize,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit);

        buildInfo.DstAccelerationStructure = handle;
        buildInfo.ScratchData = new DeviceOrHostAddressKHR
        {
            DeviceAddress = scratch.DeviceAddress
        };

        var rangeInfo = new AccelerationStructureBuildRangeInfoKHR
        {
            PrimitiveCount = primitiveCount,
            PrimitiveOffset = 0,
            FirstVertex = 0,
            TransformOffset = 0
        };
        var pRangeInfo = &rangeInfo;

        ext.CmdBuildAccelerationStructures(commandBuffer.InternalHandle, 1, in buildInfo, &pRangeInfo);
        context.RetainForExecution(commandBuffer, scratch);
        InsertBuildToReadBarrier(commandBuffer.InternalHandle);
    }

    public ulong GetDeviceAddress()
    {
        if (Handle.Handle == default)
            return 0;

        var info = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = Handle
        };
        return ext.GetAccelerationStructureDeviceAddress(context.Device, in info);
    }

    private void DisposeHandle()
    {
        if (Handle.Handle != default)
        {
            ext.DestroyAccelerationStructure(context.Device, Handle, default);
            Handle = default;
        }

        storageBuffer?.Dispose();
        storageBuffer = null;
    }

    private void InsertBuildToReadBarrier(CommandBuffer commandBuffer)
    {
        VulkanBarriers.Memory(
            context.Api,
            commandBuffer,
            AccessFlags.AccelerationStructureWriteBitKhr,
            AccessFlags.AccelerationStructureReadBitKhr | AccessFlags.ShaderReadBit,
            PipelineStageFlags.AccelerationStructureBuildBitKhr,
            PipelineStageFlags.AccelerationStructureBuildBitKhr | PipelineStageFlags.RayTracingShaderBitKhr);
    }

    public void Dispose()
    {
        DisposeHandle();
    }
}
