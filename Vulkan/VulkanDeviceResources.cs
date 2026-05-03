using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Vulkan;

internal sealed unsafe class VulkanDeviceResources : IDisposable
{
    private readonly Context context;
    private readonly List<Action> disposeActions = [];
    private bool disposed;

    public VulkanDeviceResources(Context context)
    {
        this.context = context;
    }

    public Pipeline Track(Pipeline pipeline)
    {
        if (pipeline.Handle != default)
            disposeActions.Add(() => context.Api.DestroyPipeline(context.Device, pipeline, default));
        return pipeline;
    }

    public PipelineLayout Track(PipelineLayout pipelineLayout)
    {
        if (pipelineLayout.Handle != default)
            disposeActions.Add(() => context.Api.DestroyPipelineLayout(context.Device, pipelineLayout, default));
        return pipelineLayout;
    }

    public DescriptorSetLayout Track(DescriptorSetLayout descriptorSetLayout)
    {
        if (descriptorSetLayout.Handle != default)
            disposeActions.Add(() => context.Api.DestroyDescriptorSetLayout(context.Device, descriptorSetLayout, default));
        return descriptorSetLayout;
    }

    public DescriptorPool Track(DescriptorPool descriptorPool)
    {
        if (descriptorPool.Handle != default)
            disposeActions.Add(() => context.Api.DestroyDescriptorPool(context.Device, descriptorPool, default));
        return descriptorPool;
    }

    public RenderPass Track(RenderPass renderPass)
    {
        if (renderPass.Handle != default)
            disposeActions.Add(() => context.Api.DestroyRenderPass(context.Device, renderPass, default));
        return renderPass;
    }

    public Framebuffer Track(Framebuffer framebuffer)
    {
        if (framebuffer.Handle != default)
            disposeActions.Add(() => context.Api.DestroyFramebuffer(context.Device, framebuffer, default));
        return framebuffer;
    }

    public Sampler Track(Sampler sampler)
    {
        if (sampler.Handle != default)
            disposeActions.Add(() => context.Api.DestroySampler(context.Device, sampler, default));
        return sampler;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        for (var i = disposeActions.Count - 1; i >= 0; i--)
            disposeActions[i]();

        disposeActions.Clear();
        disposed = true;
    }
}
