using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Rendering.Composition;
using Serilog;

namespace UniversalUmap.Rendering;

public sealed class Renderer : IDisposable
{
    private static readonly TimeSpan SceneCommandWaitTimeout = TimeSpan.FromSeconds(30);
    [ThreadStatic]
    private static bool isExecutingRenderOnCurrentThread;

    public Context Context { get; }
    public Scene Scene { get; }
    public Tonemapper Tonemapper { get; }
    internal Input Input => input;
    private readonly IRaytracer raytracer;
    private readonly ConcurrentQueue<ISceneCommand> pendingSceneCommands = new();
    private readonly PerspectiveCamera camera;
    private readonly Input input;
    private readonly Stopwatch inputTimer = Stopwatch.StartNew();
    private long lastInputTicks;
    private PixelSize lastCameraSize = new(1280, 720);
    private int renderThreadId = -1;
    private long sceneCommandIdCounter;

    public Renderer(ICompositionGpuInterop gpuInterop)
    {
        GpuStructLayoutValidator.ValidateOrThrow();
        input = new Input();
        camera = new PerspectiveCamera(input);
        Context = new Context(gpuInterop);
        Scene = new Scene(Context);
        LoadDefaultEnvironment();

        if (Context.RayTracingSupported)
        {
            try
            {
                raytracer = new RtxRaytracer(Context, Scene);
                Log.Information("Using RTX raytracer backend.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RTX raytracer creation failed, falling back to compute.");
                raytracer = new ComputeRaytracer(Context, Scene);
                Log.Information("Using Compute raytracer backend.");
            }
        }
        else
        {
            raytracer = new ComputeRaytracer(Context, Scene);
            Log.Information("Hardware ray tracing unsupported; using Compute raytracer backend.");
        }
        Tonemapper = new Tonemapper(Context);
    }

    private void LoadDefaultEnvironment()
    {
        const string hdriFileName = "autumn_field_puresky_4k.hdr";

        if (!EmbeddedAssets.TryReadByFileName(hdriFileName, out var embeddedHdr))
            return;

        try
        {
            var texture = TextureAsset.CreateHdr(Context, hdriFileName, embeddedHdr);
            Scene.Add(texture);
            Scene.SetEnvironment(texture);
            Log.Information("Loaded default environment from embedded asset '{HdrFileName}'.", hdriFileName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load default environment map.");
        }
    }

    internal void Render(ImageResource target)
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref renderThreadId, currentThreadId);
        isExecutingRenderOnCurrentThread = true;
        try
        {
            // Sample camera input first so scene command backlogs do not distort movement feel.
            UpdateCameraFromInput(target.Size);
            ProcessPendingSceneCommands();
            Context.Pool.FreeUsedCommandBuffers();
            RenderSceneTo(target);
        }
        finally
        {
            isExecutingRenderOnCurrentThread = false;
        }
    }

    public void UpdateScene(Action<Scene> update)
    {
        if (CanExecuteImmediatelyOnCallerThread())
        {
            update(Scene);
            return;
        }

        EnqueueAndWait(new SceneActionCommand(Interlocked.Increment(ref sceneCommandIdCounter), update, requireGpuSync: false));
    }

    public T UpdateScene<T>(Func<Scene, T> update)
    {
        if (CanExecuteImmediatelyOnCallerThread())
            return update(Scene);

        var command = new SceneFuncCommand<T>(Interlocked.Increment(ref sceneCommandIdCounter), update, requireGpuSync: false);
        EnqueueAndWait(command);
        return command.Result;
    }

    public void UpdateSceneWithGpuSync(Action<Scene> update)
    {
        if (CanExecuteImmediatelyOnCallerThread())
        {
            WaitForGpuIdleUnsafe();
            update(Scene);
            return;
        }

        EnqueueAndWait(new SceneActionCommand(Interlocked.Increment(ref sceneCommandIdCounter), update, requireGpuSync: true));
    }

    public T UpdateSceneWithGpuSync<T>(Func<Scene, T> update)
    {
        if (CanExecuteImmediatelyOnCallerThread())
        {
            WaitForGpuIdleUnsafe();
            return update(Scene);
        }

        var command = new SceneFuncCommand<T>(Interlocked.Increment(ref sceneCommandIdCounter), update, requireGpuSync: true);
        EnqueueAndWait(command);
        return command.Result;
    }

    public void FocusCamera(Matrix4x4 worldTransform, float distance = 500f)
    {
        if (CanExecuteImmediatelyOnCallerThread())
        {
            camera.FocusOn(worldTransform, distance);
            return;
        }

        EnqueueAndWait(new SceneActionCommand(
            Interlocked.Increment(ref sceneCommandIdCounter),
            _ => camera.FocusOn(worldTransform, distance),
            requireGpuSync: false));
    }

    private void RenderSceneTo(ImageResource target)
    {
        raytracer.Render(target);
        Tonemapper.Apply(raytracer.OutputColor, target);
    }

    private void UpdateCameraFromInput(PixelSize size)
    {
        lastCameraSize = size;
        var nowTicks = inputTimer.ElapsedTicks;
        var rawDeltaSeconds = lastInputTicks == 0
            ? 1f / 60f
            : (float)(nowTicks - lastInputTicks) / Stopwatch.Frequency;
        lastInputTicks = nowTicks;
        // Guard against extreme stalls (map streaming, shader compiles, breakpoints) causing camera jumps.
        var deltaSeconds = Math.Clamp(rawDeltaSeconds, 1f / 240f, 0.25f);

        var cameraChanged = camera.Update(
            size,
            deltaSeconds,
            out var cameraData);

        Scene.SetCameraData(cameraData, cameraChanged);
    }

    public void Dispose()
    {
        Tonemapper.Dispose();
        raytracer.Dispose();
        Context.Pool.FreeUsedCommandBuffers(waitForCompletion: true);
        Context.Dispose();
    }

    private void WaitForGpuIdleUnsafe()
    {
        Context.Api.DeviceWaitIdle(Context.Device).ThrowOnError();
        Context.Pool.FreeUsedCommandBuffers(waitForCompletion: true);
    }

    private bool CanExecuteImmediatelyOnCallerThread()
    {
        if (isExecutingRenderOnCurrentThread)
            return true;

        var currentThreadId = Environment.CurrentManagedThreadId;
        var currentRenderThreadId = Volatile.Read(ref renderThreadId);
        return currentRenderThreadId == -1 || currentRenderThreadId == currentThreadId;
    }

    private void EnqueueAndWait(SceneCommandBase command)
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        var currentRenderThreadId = Volatile.Read(ref renderThreadId);

        // Defensive fallback: if caller is the render thread, execute immediately to avoid deadlock.
        if (currentThreadId == currentRenderThreadId)
        {
            command.Execute(this);
            return;
        }

        pendingSceneCommands.Enqueue(command);

        if (currentThreadId == Volatile.Read(ref renderThreadId))
            ProcessPendingSceneCommands();

        if (command.Wait(SceneCommandWaitTimeout))
            return;

        var pendingCount = pendingSceneCommands.Count;
        var message =
            $"Timed out waiting for scene command #{command.CommandId} ({command.Description}) after {SceneCommandWaitTimeout.TotalSeconds:F0}s. " +
            $"CallerThread={currentThreadId}, RenderThread={currentRenderThreadId}, InRender={isExecutingRenderOnCurrentThread}, PendingCommands={pendingCount}.";
        Log.Error(message);
        throw new TimeoutException(message);
    }

    private void ProcessPendingSceneCommands()
    {
        while (pendingSceneCommands.TryDequeue(out var command))
            command.Execute(this);
    }

    private interface ISceneCommand
    {
        void Execute(Renderer renderer);
    }

    private abstract class SceneCommandBase : ISceneCommand
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long CommandId { get; }
        public string Description { get; }
        protected readonly bool RequireGpuSync;

        protected SceneCommandBase(long commandId, string description, bool requireGpuSync)
        {
            CommandId = commandId;
            Description = description;
            RequireGpuSync = requireGpuSync;
        }

        public void Execute(Renderer renderer)
        {
            var startTicks = Stopwatch.GetTimestamp();
            try
            {
                if (RequireGpuSync)
                    renderer.WaitForGpuIdleUnsafe();
                ExecuteCore(renderer.Scene);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency;
                if (elapsedMs > 250d)
                {
                    Log.Warning(
                        "Slow scene command #{CommandId} ({Description}) took {ElapsedMs:F1} ms (GpuSync={RequireGpuSync})",
                        CommandId,
                        Description,
                        elapsedMs,
                        RequireGpuSync);
                }
            }
        }

        public bool Wait(TimeSpan timeout)
        {
            if (!completion.Task.Wait(timeout))
                return false;
            return true;
        }

        protected abstract void ExecuteCore(Scene scene);
    }

    private sealed class SceneActionCommand : SceneCommandBase
    {
        private readonly Action<Scene> action;

        public SceneActionCommand(long commandId, Action<Scene> action, bool requireGpuSync)
            : base(commandId, nameof(SceneActionCommand), requireGpuSync)
        {
            this.action = action;
        }

        protected override void ExecuteCore(Scene scene) => action(scene);
    }

    private sealed class SceneFuncCommand<T> : SceneCommandBase
    {
        private readonly Func<Scene, T> func;
        public T Result { get; private set; } = default!;

        public SceneFuncCommand(long commandId, Func<Scene, T> func, bool requireGpuSync)
            : base(commandId, $"SceneFuncCommand<{typeof(T).Name}>", requireGpuSync)
        {
            this.func = func;
        }

        protected override void ExecuteCore(Scene scene) => Result = func(scene);
    }
}
