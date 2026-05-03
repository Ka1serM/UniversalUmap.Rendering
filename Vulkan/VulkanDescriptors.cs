using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Vulkan;

internal sealed unsafe class VulkanDescriptorSet : IDisposable
{
    private readonly Context context;
    private bool disposed;

    public VulkanDescriptorSet(Context context, DescriptorSetLayout layout, DescriptorSet set)
    {
        this.context = context;
        Layout = layout;
        Set = set;
    }

    public DescriptorSetLayout Layout { get; private set; }
    public DescriptorSet Set { get; private set; }

    public static DescriptorSet Allocate(Context context, DescriptorSetLayout layout, uint? variableDescriptorCount = null)
    {
        var descriptorCount = variableDescriptorCount.GetValueOrDefault();
        var variableCountInfo = new DescriptorSetVariableDescriptorCountAllocateInfo
        {
            SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
            DescriptorSetCount = 1,
            PDescriptorCounts = &descriptorCount
        };

        var layoutLocal = layout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = context.DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layoutLocal,
            PNext = variableDescriptorCount.HasValue ? &variableCountInfo : null
        };
        context.Api.AllocateDescriptorSets(context.Device, in allocInfo, out var set).ThrowOnError();
        return set;
    }

    public static void Free(Context context, ReadOnlySpan<DescriptorSet> descriptorSets)
    {
        if (descriptorSets.IsEmpty)
            return;

        fixed (DescriptorSet* pDescriptorSets = descriptorSets)
        {
            context.Api.FreeDescriptorSets(
                context.Device,
                context.DescriptorPool,
                (uint)descriptorSets.Length,
                pDescriptorSets).ThrowOnError();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        if (Set.Handle != default)
        {
            var set = Set;
            context.Api.FreeDescriptorSets(context.Device, context.DescriptorPool, 1, in set);
            Set = default;
        }

        if (Layout.Handle != default)
        {
            context.Api.DestroyDescriptorSetLayout(context.Device, Layout, default);
            Layout = default;
        }

        disposed = true;
    }
}

internal sealed unsafe class VulkanDescriptorSetBuilder
{
    private readonly List<DescriptorSetLayoutBinding> bindings = [];
    private readonly List<DescriptorBindingFlags> bindingFlags = [];
    private uint? variableDescriptorCount;

    public VulkanDescriptorSetBuilder Add(
        uint binding,
        DescriptorType descriptorType,
        uint descriptorCount,
        ShaderStageFlags stageFlags,
        DescriptorBindingFlags flags = default)
    {
        bindings.Add(new DescriptorSetLayoutBinding(binding, descriptorType, descriptorCount, stageFlags));
        bindingFlags.Add(flags);
        if ((flags & DescriptorBindingFlags.VariableDescriptorCountBit) != 0)
            variableDescriptorCount = descriptorCount;
        return this;
    }

    public VulkanDescriptorSet Build(Context context, DescriptorSetLayoutCreateFlags layoutFlags = default)
    {
        var layout = BuildLayout(context, layoutFlags);
        return new VulkanDescriptorSet(context, layout, VulkanDescriptorSet.Allocate(context, layout, variableDescriptorCount));
    }

    public DescriptorSetLayout BuildLayout(Context context, DescriptorSetLayoutCreateFlags layoutFlags = default)
    {
        if (bindings.Count == 0)
            throw new InvalidOperationException("Descriptor set layouts require at least one binding.");

        var layoutBindings = bindings.ToArray();
        var layoutBindingFlags = bindingFlags.ToArray();
        fixed (DescriptorSetLayoutBinding* pBindings = layoutBindings)
        fixed (DescriptorBindingFlags* pBindingFlags = layoutBindingFlags)
        {
            var anyBindingFlags = layoutBindingFlags.Any(flags => flags != default);
            var bindingFlagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = (uint)layoutBindingFlags.Length,
                PBindingFlags = pBindingFlags
            };

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                Flags = layoutFlags,
                BindingCount = (uint)layoutBindings.Length,
                PBindings = pBindings,
                PNext = anyBindingFlags ? &bindingFlagsInfo : null
            };
            context.Api.CreateDescriptorSetLayout(context.Device, in layoutInfo, default, out var layout).ThrowOnError();
            return layout;
        }
    }
}

internal sealed unsafe class VulkanDescriptorWriter
{
    private readonly List<WriteRequest> requests = [];

    public VulkanDescriptorWriter StorageBuffer(uint binding, VulkanBuffer buffer, ulong offset = 0, ulong? range = null)
    {
        requests.Add(new WriteRequest
        {
            Binding = binding,
            DescriptorType = DescriptorType.StorageBuffer,
            BufferInfo = new DescriptorBufferInfo(buffer.Handle, offset, range ?? buffer.Size)
        });
        return this;
    }

    public VulkanDescriptorWriter StorageImage(uint binding, VulkanImage image, ImageLayout layout = ImageLayout.General)
    {
        requests.Add(new WriteRequest
        {
            Binding = binding,
            DescriptorType = DescriptorType.StorageImage,
            ImageInfos = [new DescriptorImageInfo(default, new ImageView(image.ViewHandle), layout)]
        });
        return this;
    }

    public VulkanDescriptorWriter CombinedImageSampler(
        uint binding,
        Sampler sampler,
        VulkanImage image,
        ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal)
    {
        requests.Add(new WriteRequest
        {
            Binding = binding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            ImageInfos = [new DescriptorImageInfo(sampler, new ImageView(image.ViewHandle), layout)]
        });
        return this;
    }

    public VulkanDescriptorWriter CombinedImageSampler(uint binding, DescriptorImageInfo imageInfo)
    {
        requests.Add(new WriteRequest
        {
            Binding = binding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            ImageInfos = [imageInfo]
        });
        return this;
    }

    public VulkanDescriptorWriter CombinedImageSamplers(uint binding, ReadOnlySpan<DescriptorImageInfo> imageInfos)
    {
        requests.Add(new WriteRequest
        {
            Binding = binding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            ImageInfos = imageInfos.ToArray()
        });
        return this;
    }

    public void Update(Context context, DescriptorSet descriptorSet)
    {
        if (requests.Count == 0)
            return;

        var bufferCount = requests.Count(request => request.BufferInfo.HasValue);
        var imageCount = requests.Sum(request => request.ImageInfos?.Length ?? 0);
        var writes = new WriteDescriptorSet[requests.Count];
        var bufferInfos = new DescriptorBufferInfo[Math.Max(1, bufferCount)];
        var imageInfos = new DescriptorImageInfo[Math.Max(1, imageCount)];

        fixed (WriteDescriptorSet* pWrites = writes)
        fixed (DescriptorBufferInfo* pBufferInfos = bufferInfos)
        fixed (DescriptorImageInfo* pImageInfos = imageInfos)
        {
            var bufferIndex = 0;
            var imageIndex = 0;
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                pWrites[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSet,
                    DstBinding = request.Binding,
                    DescriptorType = request.DescriptorType
                };

                if (request.BufferInfo is { } bufferInfo)
                {
                    pBufferInfos[bufferIndex] = bufferInfo;
                    pWrites[i].DescriptorCount = 1;
                    pWrites[i].PBufferInfo = &pBufferInfos[bufferIndex];
                    bufferIndex++;
                }
                else if (request.ImageInfos is { Length: > 0 } requestImageInfos)
                {
                    for (var j = 0; j < requestImageInfos.Length; j++)
                        pImageInfos[imageIndex + j] = requestImageInfos[j];

                    pWrites[i].DescriptorCount = (uint)requestImageInfos.Length;
                    pWrites[i].PImageInfo = &pImageInfos[imageIndex];
                    imageIndex += requestImageInfos.Length;
                }
            }

            context.Api.UpdateDescriptorSets(context.Device, (uint)writes.Length, pWrites, 0, null);
        }
    }

    public static void UpdateAccelerationStructure(
        Context context,
        DescriptorSet descriptorSet,
        uint binding,
        AccelerationStructureKHR accelerationStructure)
    {
        var accelInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &accelerationStructure
        };

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = binding,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            PNext = &accelInfo
        };
        context.Api.UpdateDescriptorSets(context.Device, 1, in write, 0, null);
    }

    private struct WriteRequest
    {
        public uint Binding;
        public DescriptorType DescriptorType;
        public DescriptorBufferInfo? BufferInfo;
        public DescriptorImageInfo[]? ImageInfos;
    }
}
