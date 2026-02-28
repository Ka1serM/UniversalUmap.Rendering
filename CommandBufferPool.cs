using System;
using System.Collections.Generic;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

public sealed class CommandBufferPool : IDisposable
{
    private readonly Vk api;
    private readonly Device device;
    private readonly Queue queue;
    private readonly CommandPool commandPool;
    private readonly List<PooledCommandBuffer> usedCommandBuffers = [];
    private readonly object sync = new();

    public unsafe CommandBufferPool(Vk api, Device device, Queue queue, uint queueFamilyIndex)
    {
        this.api = api;
        this.device = device;
        this.queue = queue;

        var commandPoolCreateInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = queueFamilyIndex
        };

        api.CreateCommandPool(device, in commandPoolCreateInfo, default, out commandPool).ThrowOnError();
    }

    public unsafe void Dispose()
    {
        lock (sync)
        {
            FreeUsedCommandBuffers(waitForCompletion: true);
            api.DestroyCommandPool(device, commandPool, default);
        }
    }

    public PooledCommandBuffer CreateCommandBuffer() => new(api, device, queue, this);

    public void FreeUsedCommandBuffers()
        => FreeUsedCommandBuffers(waitForCompletion: false);

    public void FreeUsedCommandBuffers(bool waitForCompletion)
    {
        lock (sync)
        {
            for (var i = usedCommandBuffers.Count - 1; i >= 0; i--)
            {
                var commandBuffer = usedCommandBuffers[i];
                try
                {
                    if (!waitForCompletion && !commandBuffer.IsExecutionComplete())
                        continue;

                    commandBuffer.Dispose(waitForCompletion);
                    usedCommandBuffers.RemoveAt(i);
                }
                catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
                {
                    Log.Warning("Skipping command buffer disposal due to device loss.");
                    usedCommandBuffers.RemoveAt(i);
                }
            }
        }
    }

    private unsafe CommandBuffer AllocateCommandBuffer()
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            CommandBufferCount = 1,
            Level = CommandBufferLevel.Primary
        };

        lock (sync)
        {
            api.AllocateCommandBuffers(device, in allocateInfo, out CommandBuffer commandBuffer);
            return commandBuffer;
        }
    }

    private void MoveToUsed(PooledCommandBuffer commandBuffer)
    {
        lock (sync)
        {
            usedCommandBuffers.Add(commandBuffer);
        }
    }

    public sealed class PooledCommandBuffer : IDisposable
    {
        private readonly CommandBufferPool owner;
        private readonly Vk api;
        private readonly Device device;
        private readonly Queue queue;
        private readonly Fence fence;
        private List<IDisposable>? retainedResources;
        private bool started;
        private bool ended;

        public CommandBuffer InternalHandle { get; }

        internal unsafe PooledCommandBuffer(Vk api, Device device, Queue queue, CommandBufferPool owner)
        {
            this.api = api;
            this.device = device;
            this.queue = queue;
            this.owner = owner;

            InternalHandle = owner.AllocateCommandBuffer();

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit
            };

            api.CreateFence(device, in fenceInfo, default, out fence).ThrowOnError();
        }

        public IntPtr Handle => InternalHandle.Handle;

        public void BeginRecording()
        {
            if (started)
                return;

            started = true;
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            api.BeginCommandBuffer(InternalHandle, in beginInfo).ThrowOnError();
        }

        public void EndRecording()
        {
            if (!started || ended)
                return;

            ended = true;
            api.EndCommandBuffer(InternalHandle).ThrowOnError();
        }

        public unsafe void Submit(
            ReadOnlySpan<Silk.NET.Vulkan.Semaphore> waitSemaphores = default,
            ReadOnlySpan<PipelineStageFlags> waitDstStageMask = default,
            ReadOnlySpan<Silk.NET.Vulkan.Semaphore> signalSemaphores = default)
        {
            EndRecording();

            fixed (Silk.NET.Vulkan.Semaphore* pWaitSemaphores = waitSemaphores, pSignalSemaphores = signalSemaphores)
            fixed (PipelineStageFlags* pWaitStages = waitDstStageMask)
            {
                var cmd = InternalHandle;
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    WaitSemaphoreCount = waitSemaphores.IsEmpty ? 0u : (uint)waitSemaphores.Length,
                    PWaitSemaphores = pWaitSemaphores,
                    PWaitDstStageMask = pWaitStages,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmd,
                    SignalSemaphoreCount = signalSemaphores.IsEmpty ? 0u : (uint)signalSemaphores.Length,
                    PSignalSemaphores = pSignalSemaphores
                };

                var fenceValue = fence;
                lock (owner.sync)
                {
                    // Vulkan queue operations must be externally synchronized.
                    api.ResetFences(device, 1, in fenceValue).ThrowOnError();
                    api.QueueSubmit(queue, 1, in submitInfo, fenceValue).ThrowOnError();
                }
            }

            owner.MoveToUsed(this);
        }

        public void RetainForExecution(IDisposable resource)
        {
            if (resource is null)
                throw new ArgumentNullException(nameof(resource));

            retainedResources ??= new List<IDisposable>(2);
            retainedResources.Add(resource);
        }

        public bool IsExecutionComplete()
        {
            var result = api.GetFenceStatus(device, fence);
            return result == Result.Success;
        }

        public unsafe void Dispose(bool waitForCompletion)
        {
            try
            {
                if (waitForCompletion)
                    api.WaitForFences(device, 1, in fence, true, ulong.MaxValue).ThrowOnError();
                else if (!IsExecutionComplete())
                    return;
            }
            catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
            {
                // Device is lost: best effort cleanup only.
                return;
            }

            var handle = InternalHandle;
            api.FreeCommandBuffers(device, owner.commandPool, 1, in handle);
            api.DestroyFence(device, fence, default);

            if (retainedResources is not null)
            {
                foreach (var resource in retainedResources)
                {
                    try
                    {
                        resource.Dispose();
                    }
                    catch
                    {
                        // Best-effort cleanup only.
                    }
                }
                retainedResources.Clear();
            }
        }

        public void Dispose()
        {
            Dispose(waitForCompletion: true);
        }
    }
}
