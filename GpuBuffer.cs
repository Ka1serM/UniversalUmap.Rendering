using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

internal sealed unsafe class GpuBuffer : IDisposable
{
    private readonly Vk api;
    private readonly Device device;

    public Silk.NET.Vulkan.Buffer Handle { get; private set; }
    public DeviceMemory Memory { get; private set; }
    public ulong Size { get; }
    public ulong DeviceAddress { get; }

    public GpuBuffer(
        Context context,
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags memoryFlags,
        ReadOnlySpan<byte> initialData = default)
    {
        api = context.Api;
        device = context.Device;
        Size = size;

        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        api.CreateBuffer(device, in createInfo, default, out var buffer).ThrowOnError();
        Handle = buffer;

        api.GetBufferMemoryRequirements(device, buffer, out var requirements);
        var memoryTypeIndex = MemoryHelper.FindSuitableMemoryTypeIndex(
            api,
            context.PhysicalDevice,
            requirements.MemoryTypeBits,
            memoryFlags);

        if (memoryTypeIndex < 0)
            throw new InvalidOperationException("Could not find a suitable Vulkan memory type for Vulkan buffer");

        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = (uint)memoryTypeIndex
        };

        var bdaInfo = new MemoryAllocateFlagsInfo
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit
        };
        if (usage.HasFlag(BufferUsageFlags.ShaderDeviceAddressBit))
            allocateInfo.PNext = &bdaInfo;

        api.AllocateMemory(device, in allocateInfo, default, out var memory).ThrowOnError();
        Memory = memory;
        api.BindBufferMemory(device, buffer, memory, 0).ThrowOnError();

        if (!initialData.IsEmpty)
        {
            void* mapped;
            api.MapMemory(device, memory, 0, requirements.Size, 0, &mapped).ThrowOnError();
            fixed (byte* src = initialData)
            {
                System.Buffer.MemoryCopy(src, mapped, initialData.Length, initialData.Length);
            }
            api.UnmapMemory(device, memory);
        }

        if (usage.HasFlag(BufferUsageFlags.ShaderDeviceAddressBit))
        {
            var bda = new BufferDeviceAddressInfo
            {
                SType = StructureType.BufferDeviceAddressInfo,
                Buffer = buffer
            };
            DeviceAddress = api.GetBufferDeviceAddress(device, in bda);
        }
    }

    public void Dispose()
    {
        if (Memory.Handle != default)
        {
            api.FreeMemory(device, Memory, default);
            Memory = default;
        }

        if (Handle.Handle != default)
        {
            api.DestroyBuffer(device, Handle, default);
            Handle = default;
        }
    }

    public void Upload(ReadOnlySpan<byte> data, ulong offset = 0)
    {
        if (data.IsEmpty)
            return;
        if (offset + (ulong)data.Length > Size)
            throw new ArgumentOutOfRangeException(nameof(data), "Upload range exceeds buffer size.");

        void* mapped;
        api.MapMemory(device, Memory, offset, (ulong)data.Length, 0, &mapped).ThrowOnError();
        fixed (byte* src = data)
        {
            System.Buffer.MemoryCopy(src, mapped, data.Length, data.Length);
        }
        api.UnmapMemory(device, Memory);
    }
}
