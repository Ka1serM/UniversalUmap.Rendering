using System;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace UniversalUmap.Rendering;

internal sealed unsafe class Accel : IDisposable
{
    private readonly Context context;
    private readonly KhrAccelerationStructure ext;
    private GpuBuffer? storageBuffer;

    public AccelerationStructureKHR Handle { get; private set; }
    public AccelerationStructureTypeKHR Type { get; private set; }

    public Accel(Context context, KhrAccelerationStructure ext)
    {
        this.context = context;
        this.ext = ext;
    }

    public void BuildTopLevel(uint primitiveCount, ulong instancesDeviceAddress)
    {
        DisposeHandle();

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

        storageBuffer = new GpuBuffer(
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

        var scratch = new GpuBuffer(
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

        var commandBuffer = context.Pool.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        ext.CmdBuildAccelerationStructures(commandBuffer.InternalHandle, 1, in buildInfo, &pRangeInfo);
        commandBuffer.Submit();
        commandBuffer.RetainForExecution(scratch);
    }

    public void BuildBottomLevelTriangles(
        uint primitiveCount,
        ulong vertexAddress,
        ulong vertexStride,
        uint maxVertex,
        ulong indexAddress)
    {
        DisposeHandle();

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

        storageBuffer = new GpuBuffer(
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

        var scratch = new GpuBuffer(
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

        var commandBuffer = context.Pool.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        ext.CmdBuildAccelerationStructures(commandBuffer.InternalHandle, 1, in buildInfo, &pRangeInfo);
        commandBuffer.Submit();
        commandBuffer.RetainForExecution(scratch);
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

    public void Dispose()
    {
        DisposeHandle();
    }
}
