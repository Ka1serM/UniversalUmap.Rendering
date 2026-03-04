using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Rendering.Composition;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

public sealed class Renderer : IDisposable
{
    public Context Context { get; }
    public Scene Scene { get; }
    public OverlayCompositor OverlayCompositor { get; }

    internal Input Input { get; }
    internal GpuScenePicker Picker { get; }

    private readonly GpuRaytracer raytracer;
    private readonly Stopwatch renderTimer = Stopwatch.StartNew();
    private long lastRenderTicks;

    public Renderer(ICompositionGpuInterop gpuInterop)
    {
        GpuStructLayoutValidator.ValidateOrThrow();

        Input = new Input();
        Context = new Context(gpuInterop);
        Scene = new Scene(Context, Input);
        Scene.TryLoadDefaultEnvironment();

        raytracer = GpuRaytracer.Create(Context, Scene);
        Picker = new GpuScenePicker(Scene, raytracer);
        OverlayCompositor = new OverlayCompositor(Context);
    }

    internal void Render(ImageResource target)
    {
        var nowTicks = renderTimer.ElapsedTicks;
        var rawDeltaSeconds = lastRenderTicks == 0 ? 1f / 60f: (float)(nowTicks - lastRenderTicks) / Stopwatch.Frequency;
        lastRenderTicks = nowTicks;
        var deltaSeconds = Math.Clamp(rawDeltaSeconds, 1f / 240f, 0.25f);

        lock (Scene.SyncRoot)
        {
            Scene.Update(target.Size, deltaSeconds);

            var commandBuffer = Context.Pool.CreateCommandBuffer();
            commandBuffer.BeginRecording();
            raytracer.Record(target, commandBuffer);

            var selectedInstanceId = Scene.SelectedInstanceIndex >= 0
                ? (uint)Scene.SelectedInstanceIndex
                : SharedShaderDefines.InvalidInstance;
            OverlayCompositor.Record(
                commandBuffer,
                raytracer.OutputColor,
                raytracer.OutputCrypto,
                raytracer.OutputPosition,
                selectedInstanceId,
                target);
            commandBuffer.Submit();
        }
    }

    internal bool TryGetBindlessTextureDescriptors(out DescriptorSetLayout layout, out DescriptorSet set)
    {
        lock (Scene.SyncRoot)
            return raytracer.TryGetBindlessTextureDescriptors(out layout, out set);
    }

    public void Dispose()
    {
        lock (Scene.SyncRoot)
        {
            OverlayCompositor.Dispose();
            raytracer.Dispose();
            Scene.Dispose();
            Context.Pool.WaitForSubmittedCommandBuffers();
            Context.Dispose();
        }
    }
}
