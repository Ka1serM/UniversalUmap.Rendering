using System;
using System.Collections.Generic;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Vulkan;

internal sealed unsafe class VulkanRasterShaderProgram : IDisposable
{
    private readonly Context context;
    private readonly VulkanDeviceResources deviceResources;
    private readonly ShaderStageFlags pushConstantStages;
    private readonly PipelineLayout pipelineLayout;
    private readonly Pipeline pipeline;
    private readonly RenderPass renderPass;
    private readonly bool hasVertexInput;
    private readonly DescriptorSet externalDescriptorSet;
    private readonly Dictionary<ulong, Framebuffer> framebuffersByTargetViewHandle = [];

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
        deviceResources = new VulkanDeviceResources(context);
        this.pushConstantStages = pushConstantStages;
        hasVertexInput = !vertexBindings.IsEmpty || !vertexAttributes.IsEmpty;
        this.externalDescriptorSet = externalDescriptorSet;
        Log.Debug(
            "Creating VulkanRasterShaderProgram: VS={VertexShader} FS={FragmentShader} VertexInput={HasVertexInput} ExternalSet={HasExternalSet}",
            vertexShaderSpvAsset,
            fragmentShaderSpvAsset,
            hasVertexInput,
            externalDescriptorSet.Handle != default);

        using var shaderModules = VulkanPipelineFactory.CreateShaderModuleSet(context, vertexShaderSpvAsset, fragmentShaderSpvAsset);
        using var mainName = new ByteString("main");

        var colorAttachment = new AttachmentDescription
        {
            Format = Format.R8G8B8A8Unorm,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.ColorAttachmentOptimal
        };
        var colorAttachmentReference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentReference
        };
        var subpassDependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };
        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &subpassDependency
        };
        context.Api.CreateRenderPass(context.Device, in renderPassInfo, default, out var renderPassLocal).ThrowOnError();
        renderPass = deviceResources.Track(renderPassLocal);

        PushConstantRange? pushRange = null;
        if (pushConstantSize > 0)
        {
            pushRange = new PushConstantRange
            {
                StageFlags = pushConstantStages,
                Offset = 0,
                Size = pushConstantSize
            };
        }

        Span<DescriptorSetLayout> setLayouts = stackalloc DescriptorSetLayout[1];
        setLayouts[0] = externalDescriptorSetLayout;
        var setLayoutSpan = externalDescriptorSetLayout.Handle != default
            ? setLayouts
            : ReadOnlySpan<DescriptorSetLayout>.Empty;
        pipelineLayout = deviceResources.Track(VulkanPipelineFactory.CreatePipelineLayout(context, setLayoutSpan, pushRange));
        pipeline = default;

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = shaderModules.Vertex,
            PName = mainName
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = shaderModules.Fragment,
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
            var colorBlendAttachment = new PipelineColorBlendAttachmentState
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
                PAttachments = &colorBlendAttachment
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
                RenderPass = renderPass,
                Subpass = 0,
                PNext = null
            };
            context.Api.CreateGraphicsPipelines(context.Device, default, 1, in pipelineInfo, default, out var pipelineLocal).ThrowOnError();
            pipeline = deviceResources.Track(pipelineLocal);
        }
    }

    public void Draw<TPushConstants>(
        VulkanImage target,
        uint vertexCount,
        VulkanBuffer? vertexBuffer,
        uint firstVertex,
        in TPushConstants pushConstants)
        where TPushConstants : unmanaged
    {
        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);
        Log.Debug("Raster shader draw: target={Width}x{Height} vertexCount={VertexCount}", target.Size.Width, target.Size.Height, vertexCount);
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
        context.SubmitAndWait(commandBuffer);
    }

    public void DrawIndexed<TPushConstants>(
        VulkanImage target,
        uint indexCount,
        VulkanBuffer vertexBuffer,
        VulkanBuffer indexBuffer,
        IndexType indexType,
        in TPushConstants pushConstants)
        where TPushConstants : unmanaged
    {
        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);
        Log.Debug("Raster shader indexed draw: target={Width}x{Height} indexCount={IndexCount}", target.Size.Width, target.Size.Height, indexCount);
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
        context.SubmitAndWait(commandBuffer);
    }

    private void BeginRenderPass(Context.CommandBuffer commandBuffer, VulkanImage target)
    {
        target.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit);

        var renderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Math.Max(1, target.Size.Width), (uint)Math.Max(1, target.Size.Height)));
        var clearValue = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };
        var framebuffer = GetOrCreateFramebuffer(target);
        var beginInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = renderArea,
            ClearValueCount = 1,
            PClearValues = &clearValue
        };

        context.Api.CmdBeginRenderPass(commandBuffer.InternalHandle, in beginInfo, SubpassContents.Inline);

        var viewport = new Viewport(0, 0, target.Size.Width, target.Size.Height, 0f, 1f);
        var scissor = renderArea;
        context.Api.CmdSetViewport(commandBuffer.InternalHandle, 0, 1, in viewport);
        context.Api.CmdSetScissor(commandBuffer.InternalHandle, 0, 1, in scissor);
        context.Api.CmdBindPipeline(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, pipeline);
        if (externalDescriptorSet.Handle != default)
            context.Api.CmdBindDescriptorSets(commandBuffer.InternalHandle, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, in externalDescriptorSet, 0, null);
    }

    private void EndRenderPass(Context.CommandBuffer commandBuffer, VulkanImage target)
    {
        context.Api.CmdEndRenderPass(commandBuffer.InternalHandle);
        target.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
    }

    private Framebuffer GetOrCreateFramebuffer(VulkanImage target)
    {
        if (framebuffersByTargetViewHandle.TryGetValue(target.ViewHandle, out var framebuffer))
            return framebuffer;

        unsafe
        {
            var imageView = new ImageView(target.ViewHandle);
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = &imageView,
                Width = (uint)Math.Max(1, target.Size.Width),
                Height = (uint)Math.Max(1, target.Size.Height),
                Layers = 1
            };
            context.Api.CreateFramebuffer(context.Device, in framebufferInfo, default, out framebuffer).ThrowOnError();
            framebuffer = deviceResources.Track(framebuffer);
        }

        framebuffersByTargetViewHandle[target.ViewHandle] = framebuffer;
        return framebuffer;
    }

    public void Dispose()
    {
        framebuffersByTargetViewHandle.Clear();
        deviceResources.Dispose();
    }
}
