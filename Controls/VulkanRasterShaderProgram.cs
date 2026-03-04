using System;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Controls;

internal sealed unsafe class VulkanRasterShaderProgram : IDisposable
{
    private readonly Context context;
    private readonly ShaderStageFlags pushConstantStages;
    private readonly PipelineLayout pipelineLayout;
    private readonly Pipeline pipeline;
    private readonly bool hasVertexInput;
    private readonly DescriptorSet externalDescriptorSet;

    public VulkanRasterShaderProgram(
        Context context,
        string vertexShaderSpvAsset,
        string fragmentShaderSpvAsset,
        PrimitiveTopology topology,
        ShaderStageFlags pushConstantStages,
        uint pushConstantSize,
        ReadOnlySpan<VertexInputBindingDescription> vertexBindings = default,
        ReadOnlySpan<VertexInputAttributeDescription> vertexAttributes = default,
        bool enableAlphaBlending = true,
        DescriptorSetLayout externalDescriptorSetLayout = default,
        DescriptorSet externalDescriptorSet = default)
    {
        this.context = context;
        this.pushConstantStages = pushConstantStages;
        hasVertexInput = !vertexBindings.IsEmpty || !vertexAttributes.IsEmpty;
        this.externalDescriptorSet = externalDescriptorSet;

        var vertBytes = EmbeddedAssets.ReadByFileName(vertexShaderSpvAsset);
        var fragBytes = EmbeddedAssets.ReadByFileName(fragmentShaderSpvAsset);
        using var mainName = new ByteString("main");

        ShaderModule vertModule;
        ShaderModule fragModule;
        fixed (byte* pVert = vertBytes)
        fixed (byte* pFrag = fragBytes)
        {
            var vertInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)vertBytes.Length,
                PCode = (uint*)pVert
            };
            context.Api.CreateShaderModule(context.Device, in vertInfo, default, out vertModule).ThrowOnError();

            var fragInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)fragBytes.Length,
                PCode = (uint*)pFrag
            };
            context.Api.CreateShaderModule(context.Device, in fragInfo, default, out fragModule).ThrowOnError();
        }

        PushConstantRange pushRange = default;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo
        };
        if (externalDescriptorSetLayout.Handle != default)
        {
            layoutInfo.SetLayoutCount = 1;
            layoutInfo.PSetLayouts = &externalDescriptorSetLayout;
        }
        if (pushConstantSize > 0)
        {
            pushRange = new PushConstantRange
            {
                StageFlags = pushConstantStages,
                Offset = 0,
                Size = pushConstantSize
            };
            layoutInfo.PushConstantRangeCount = 1;
            layoutInfo.PPushConstantRanges = &pushRange;
        }
        context.Api.CreatePipelineLayout(context.Device, in layoutInfo, default, out pipelineLayout).ThrowOnError();

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertModule,
            PName = mainName
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragModule,
            PName = mainName
        };

        fixed (VertexInputBindingDescription* pBindings = vertexBindings)
        fixed (VertexInputAttributeDescription* pAttributes = vertexAttributes)
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)vertexBindings.Length,
                PVertexBindingDescriptions = pBindings,
                VertexAttributeDescriptionCount = (uint)vertexAttributes.Length,
                PVertexAttributeDescriptions = pAttributes
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = topology
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false,
                DepthWriteEnable = false,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false
            };
            var colorAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = enableAlphaBlending,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment
            };

            var dynamicStates = stackalloc DynamicState[2];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };

            var colorFormat = Format.B8G8R8A8Unorm;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamicState,
                Layout = pipelineLayout,
                RenderPass = default,
                Subpass = 0,
                PNext = &renderingInfo
            };
            context.Api.CreateGraphicsPipelines(context.Device, default, 1, in pipelineInfo, default, out pipeline).ThrowOnError();
        }

        context.Api.DestroyShaderModule(context.Device, vertModule, default);
        context.Api.DestroyShaderModule(context.Device, fragModule, default);
    }

    public void Draw<TPushConstants>(
        ImageResource target,
        uint vertexCount,
        GpuBuffer? vertexBuffer,
        uint firstVertex,
        in TPushConstants pushConstants)
        where TPushConstants : unmanaged
    {
        var commandBuffer = context.Pool.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        BeginRenderPass(commandBuffer, target);

        if (hasVertexInput && vertexBuffer is not null)
        {
            var vb = vertexBuffer.Handle;
            ulong offset = 0;
            context.Api.CmdBindVertexBuffers(commandBuffer.InternalHandle, 0, 1, in vb, in offset);
        }

        fixed (TPushConstants* pPush = &pushConstants)
        {
            context.Api.CmdPushConstants(
                commandBuffer.InternalHandle,
                pipelineLayout,
                pushConstantStages,
                0,
                (uint)sizeof(TPushConstants),
                pPush);
        }

        context.Api.CmdDraw(commandBuffer.InternalHandle, vertexCount, 1, firstVertex, 0);
        EndRenderPass(commandBuffer, target);
        commandBuffer.Submit();
    }

    public void DrawIndexed<TPushConstants>(
        ImageResource target,
        uint indexCount,
        GpuBuffer vertexBuffer,
        GpuBuffer indexBuffer,
        IndexType indexType,
        in TPushConstants pushConstants)
        where TPushConstants : unmanaged
    {
        var commandBuffer = context.Pool.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        BeginRenderPass(commandBuffer, target);

        var vb = vertexBuffer.Handle;
        ulong vbOffset = 0;
        context.Api.CmdBindVertexBuffers(commandBuffer.InternalHandle, 0, 1, in vb, in vbOffset);
        context.Api.CmdBindIndexBuffer(commandBuffer.InternalHandle, indexBuffer.Handle, 0, indexType);

        fixed (TPushConstants* pPush = &pushConstants)
        {
            context.Api.CmdPushConstants(
                commandBuffer.InternalHandle,
                pipelineLayout,
                pushConstantStages,
                0,
                (uint)sizeof(TPushConstants),
                pPush);
        }

        context.Api.CmdDrawIndexed(commandBuffer.InternalHandle, indexCount, 1, 0, 0, 0);
        EndRenderPass(commandBuffer, target);
        commandBuffer.Submit();
    }

    private void BeginRenderPass(CommandBufferPool.PooledCommandBuffer commandBuffer, ImageResource target)
    {
        target.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);

        var clear = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = new ImageView(target.ViewHandle),
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = clear
        };
        var renderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Math.Max(1, target.Size.Width), (uint)Math.Max(1, target.Size.Height)));
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = renderArea,
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment
        };

        context.Api.CmdBeginRendering(commandBuffer.InternalHandle, in renderingInfo);

        var viewport = new Viewport(0, 0, target.Size.Width, target.Size.Height, 0f, 1f);
        var scissor = renderArea;
        context.Api.CmdSetViewport(commandBuffer.InternalHandle, 0, 1, in viewport);
        context.Api.CmdSetScissor(commandBuffer.InternalHandle, 0, 1, in scissor);
        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, pipeline);
        if (externalDescriptorSet.Handle != default)
            context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, in externalDescriptorSet, 0, null);
    }

    private void EndRenderPass(CommandBufferPool.PooledCommandBuffer commandBuffer, ImageResource target)
    {
        context.Api.CmdEndRendering(commandBuffer.InternalHandle);
        target.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.General, AccessFlags.MemoryReadBit);
    }

    public void Dispose()
    {
        if (pipeline.Handle != default)
            context.Api.DestroyPipeline(context.Device, pipeline, default);
        if (pipelineLayout.Handle != default)
            context.Api.DestroyPipelineLayout(context.Device, pipelineLayout, default);
    }
}
