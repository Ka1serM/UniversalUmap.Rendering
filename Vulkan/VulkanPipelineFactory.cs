using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Vulkan;

internal static unsafe class VulkanPipelineFactory
{
    public sealed class ShaderModuleSet : IDisposable
    {
        private readonly Context context;
        private bool disposed;

        public ShaderModuleSet(Context context, ShaderModule vertex, ShaderModule fragment)
        {
            this.context = context;
            Vertex = vertex;
            Fragment = fragment;
        }

        public ShaderModule Vertex { get; }
        public ShaderModule Fragment { get; }

        public void Dispose()
        {
            if (disposed)
                return;

            if (Vertex.Handle != default)
                context.Api.DestroyShaderModule(context.Device, Vertex, default);
            if (Fragment.Handle != default)
                context.Api.DestroyShaderModule(context.Device, Fragment, default);
            disposed = true;
        }
    }

    public static PipelineLayout CreatePipelineLayout(
        Context context,
        ReadOnlySpan<DescriptorSetLayout> descriptorSetLayouts,
        PushConstantRange? pushConstantRange = null)
    {
        fixed (DescriptorSetLayout* setLayouts = descriptorSetLayouts)
        {
            var range = pushConstantRange.GetValueOrDefault();
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)descriptorSetLayouts.Length,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = pushConstantRange.HasValue ? 1u : 0u,
                PPushConstantRanges = pushConstantRange.HasValue ? &range : null
            };

            context.Api.CreatePipelineLayout(context.Device, in layoutInfo, default, out var layout).ThrowOnError();
            return layout;
        }
    }

    public static ShaderModule CreateShaderModule(Context context, string assetName)
    {
        return CreateShaderModule(context, EmbeddedAssets.ReadByFileName(assetName));
    }

    public static ShaderModuleSet CreateShaderModuleSet(Context context, string vertexShaderAssetName, string fragmentShaderAssetName)
    {
        return new ShaderModuleSet(
            context,
            CreateShaderModule(context, vertexShaderAssetName),
            CreateShaderModule(context, fragmentShaderAssetName));
    }

    public static ShaderModule CreateShaderModule(Context context, ReadOnlySpan<byte> shaderBytes)
    {
        fixed (byte* shader = shaderBytes)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)shaderBytes.Length,
                PCode = (uint*)shader
            };
            context.Api.CreateShaderModule(context.Device, in info, default, out var module).ThrowOnError();
            return module;
        }
    }

    public static Pipeline CreateComputePipeline(Context context, PipelineLayout layout, string assetName, string entryPoint = "main")
    {
        var module = CreateShaderModule(context, assetName);
        try
        {
            using var entryPointName = new ByteString(entryPoint);
            var stageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = entryPointName
            };
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stageInfo,
                Layout = layout
            };
            context.Api.CreateComputePipelines(context.Device, default, 1, in pipelineInfo, default, out var pipeline).ThrowOnError();
            return pipeline;
        }
        finally
        {
            context.Api.DestroyShaderModule(context.Device, module, default);
        }
    }
}
