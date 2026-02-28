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
    private static readonly TimeSpan SceneCommandWaitTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SceneCommandWaitSlice = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SceneCommandEnqueueTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SceneCommandDrainStallThreshold = TimeSpan.FromSeconds(2);
    private const int MaxPendingSceneCommands = 8192;
    private const int MaxSceneCommandsPerFrame = 512;
    private const double MaxSceneCommandDrainMsPerFrame = 3.0;
    private const int MaxUploadBytesPerFrame = 32 * 1024 * 1024;
    private const int MaxMeshUploadsPerFrame = 32;
    private const int MaxInstanceUploadsPerFrame = 2048;
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
    private readonly SemaphoreSlim sceneCommandSlots = new(MaxPendingSceneCommands, MaxPendingSceneCommands);
    private long lastInputTicks;
    private PixelSize lastCameraSize = new(1280, 720);
    private int renderThreadId = -1;
    private long sceneCommandIdCounter;
    private long lastSceneCommandDrainTicks = Stopwatch.GetTimestamp();
    private long lastSceneCommandStallLogTicks;
    private int renderActive;
    private int drainActive;

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
        Volatile.Write(ref renderActive, 1);
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
            Volatile.Write(ref renderActive, 0);
        }
    }

    public void UpdateScene(Action<Scene> update)
    {
        if (CanExecuteImmediatelyOnCallerThread())
        {
            update(Scene);
            return;
        }

        EnqueueAndWait(new SceneActionCommand(Interlocked.Increment(ref sceneCommandIdCounter), update, requireGpuSync: false, 0, 0, 0));
    }

    public void UpdateScene(
        Action<Scene> update,
        int estimatedUploadBytes,
        int estimatedMeshAdds,
        int estimatedInstanceAdds)
    {
        if (CanExecuteImmediatelyOnCallerThread())
        {
            update(Scene);
            return;
        }

        EnqueueAndWait(new SceneActionCommand(
            Interlocked.Increment(ref sceneCommandIdCounter),
            update,
            requireGpuSync: false,
            estimatedUploadBytes,
            estimatedMeshAdds,
            estimatedInstanceAdds));
    }

    public void EnqueueSceneUpdate(
        Action<Scene> update,
        int estimatedUploadBytes = 0,
        int estimatedMeshAdds = 0,
        int estimatedInstanceAdds = 0)
    {
        if (CanExecuteImmediatelyOnCallerThread())
        {
            update(Scene);
            return;
        }

        EnqueueCommand(new FireAndForgetSceneActionCommand(
            Interlocked.Increment(ref sceneCommandIdCounter),
            update,
            requireGpuSync: false,
            estimatedUploadBytes,
            estimatedMeshAdds,
            estimatedInstanceAdds));
    }

    public T UpdateScene<T>(Func<Scene, T> update)
    {
        if (CanExecuteImmediatelyOnCallerThread())
            return update(Scene);

        var command = new SceneFuncCommand<T>(Interlocked.Increment(ref sceneCommandIdCounter), update, requireGpuSync: false, 0, 0, 0);
        EnqueueAndWait(command);
        return command.Result;
    }

    public T UpdateScene<T>(
        Func<Scene, T> update,
        int estimatedUploadBytes,
        int estimatedMeshAdds,
        int estimatedInstanceAdds)
    {
        if (CanExecuteImmediatelyOnCallerThread())
            return update(Scene);

        var command = new SceneFuncCommand<T>(
            Interlocked.Increment(ref sceneCommandIdCounter),
            update,
            requireGpuSync: false,
            estimatedUploadBytes,
            estimatedMeshAdds,
            estimatedInstanceAdds);
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

        EnqueueAndWait(new SceneActionCommand(Interlocked.Increment(ref sceneCommandIdCounter), update, requireGpuSync: true, 0, 0, 0));
    }

    public T UpdateSceneWithGpuSync<T>(Func<Scene, T> update)
    {
        if (CanExecuteImmediatelyOnCallerThread())
        {
            WaitForGpuIdleUnsafe();
            return update(Scene);
        }

        var command = new SceneFuncCommand<T>(Interlocked.Increment(ref sceneCommandIdCounter), update, requireGpuSync: true, 0, 0, 0);
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
            requireGpuSync: false,
            0,
            0,
            0));
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
        // Fence-based drain of submitted command buffers avoids a full device-wide idle stall.
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

        EnqueueCommand(command);

        if (currentThreadId == Volatile.Read(ref renderThreadId))
            ProcessPendingSceneCommands();

        var waitStart = Stopwatch.GetTimestamp();
        while (true)
        {
            if (command.Wait(SceneCommandWaitSlice))
                return;

            // If render loop is not actively running, allow waiting thread to help drain.
            if (Volatile.Read(ref renderActive) == 0)
                TryProcessPendingSceneCommandsFromAnyThread();

            var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - waitStart) / (double)Stopwatch.Frequency);
            if (elapsed < SceneCommandWaitTimeout)
                continue;

            var pendingCount = pendingSceneCommands.Count;
            var message =
                $"Timed out waiting for scene command #{command.CommandId} ({command.Description}) after {SceneCommandWaitTimeout.TotalSeconds:F0}s. " +
                $"CallerThread={currentThreadId}, RenderThread={currentRenderThreadId}, InRender={Volatile.Read(ref renderActive) != 0}, PendingCommands={pendingCount}.";
            Log.Error(message);
            throw new TimeoutException(message);
        }
    }

    private void ProcessPendingSceneCommands()
    {
        if (Interlocked.CompareExchange(ref drainActive, 1, 0) != 0)
            return;

        try
        {
            ProcessPendingSceneCommandsCore();
        }
        finally
        {
            Volatile.Write(ref drainActive, 0);
        }
    }

    private void TryProcessPendingSceneCommandsFromAnyThread()
    {
        if (Interlocked.CompareExchange(ref drainActive, 1, 0) != 0)
            return;

        try
        {
            ProcessPendingSceneCommandsCore();
        }
        finally
        {
            Volatile.Write(ref drainActive, 0);
        }
    }

    private void ProcessPendingSceneCommandsCore()
    {
        var startTicks = Stopwatch.GetTimestamp();
        var processed = 0;
        var uploadBytes = 0;
        var meshAdds = 0;
        var instanceAdds = 0;
        while (processed < MaxSceneCommandsPerFrame &&
               (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency < MaxSceneCommandDrainMsPerFrame &&
               pendingSceneCommands.TryPeek(out var nextCommand))
        {
            var exceedsBudget =
                processed > 0 &&
                ((uploadBytes + nextCommand.EstimatedUploadBytes > MaxUploadBytesPerFrame) ||
                 (meshAdds + nextCommand.EstimatedMeshAdds > MaxMeshUploadsPerFrame) ||
                 (instanceAdds + nextCommand.EstimatedInstanceAdds > MaxInstanceUploadsPerFrame));
            if (exceedsBudget)
                break;

            if (!pendingSceneCommands.TryDequeue(out var command))
                continue;
            command.Execute(this);
            uploadBytes += command.EstimatedUploadBytes;
            meshAdds += command.EstimatedMeshAdds;
            instanceAdds += command.EstimatedInstanceAdds;
            processed++;
        }

        if (processed > 0 || pendingSceneCommands.IsEmpty)
            Volatile.Write(ref lastSceneCommandDrainTicks, Stopwatch.GetTimestamp());
    }

    private void EnqueueCommand(ISceneCommand command)
    {
        if (!sceneCommandSlots.Wait(SceneCommandEnqueueTimeout))
        {
            var nowTicks = Stopwatch.GetTimestamp();
            var lastDrainTicks = Volatile.Read(ref lastSceneCommandDrainTicks);
            var stalledFor = TimeSpan.FromSeconds((nowTicks - lastDrainTicks) / (double)Stopwatch.Frequency);
            var message =
                $"Timed out enqueueing scene command after {SceneCommandEnqueueTimeout.TotalSeconds:F0}s. " +
                $"PendingCommands={pendingSceneCommands.Count}, RenderThread={Volatile.Read(ref renderThreadId)}, StalledForMs={stalledFor.TotalMilliseconds:F0}.";
            Log.Error(message);
            throw new TimeoutException(message);
        }

        var wrapper = new QueueSlotReleaseCommand(command, sceneCommandSlots);
        pendingSceneCommands.Enqueue(wrapper);

        var now = Stopwatch.GetTimestamp();
        var sinceDrain = TimeSpan.FromSeconds((now - Volatile.Read(ref lastSceneCommandDrainTicks)) / (double)Stopwatch.Frequency);
        if (sinceDrain >= SceneCommandDrainStallThreshold &&
            now - Volatile.Read(ref lastSceneCommandStallLogTicks) > Stopwatch.Frequency)
        {
            Volatile.Write(ref lastSceneCommandStallLogTicks, now);
            Log.Warning(
                "Scene command ingestion is back-pressured. PendingCommands={PendingCommands}, StalledForMs={StalledForMs:F0}.",
                pendingSceneCommands.Count,
                sinceDrain.TotalMilliseconds);
        }
    }

    private interface ISceneCommand
    {
        void Execute(Renderer renderer);
        int EstimatedUploadBytes { get; }
        int EstimatedMeshAdds { get; }
        int EstimatedInstanceAdds { get; }
    }

    private sealed class QueueSlotReleaseCommand : ISceneCommand
    {
        private readonly ISceneCommand inner;
        private readonly SemaphoreSlim slots;

        public QueueSlotReleaseCommand(ISceneCommand inner, SemaphoreSlim slots)
        {
            this.inner = inner;
            this.slots = slots;
        }

        public void Execute(Renderer renderer)
        {
            try
            {
                inner.Execute(renderer);
            }
            finally
            {
                slots.Release();
            }
        }

        public int EstimatedUploadBytes => inner.EstimatedUploadBytes;
        public int EstimatedMeshAdds => inner.EstimatedMeshAdds;
        public int EstimatedInstanceAdds => inner.EstimatedInstanceAdds;
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

        public virtual int EstimatedUploadBytes => 0;
        public virtual int EstimatedMeshAdds => 0;
        public virtual int EstimatedInstanceAdds => 0;
    }

    private sealed class SceneActionCommand : SceneCommandBase
    {
        private readonly Action<Scene> action;
        private readonly int estimatedUploadBytes;
        private readonly int estimatedMeshAdds;
        private readonly int estimatedInstanceAdds;

        public SceneActionCommand(
            long commandId,
            Action<Scene> action,
            bool requireGpuSync,
            int estimatedUploadBytes,
            int estimatedMeshAdds,
            int estimatedInstanceAdds)
            : base(commandId, nameof(SceneActionCommand), requireGpuSync)
        {
            this.action = action;
            this.estimatedUploadBytes = Math.Max(0, estimatedUploadBytes);
            this.estimatedMeshAdds = Math.Max(0, estimatedMeshAdds);
            this.estimatedInstanceAdds = Math.Max(0, estimatedInstanceAdds);
        }

        protected override void ExecuteCore(Scene scene) => action(scene);
        public override int EstimatedUploadBytes => estimatedUploadBytes;
        public override int EstimatedMeshAdds => estimatedMeshAdds;
        public override int EstimatedInstanceAdds => estimatedInstanceAdds;
    }

    private sealed class SceneFuncCommand<T> : SceneCommandBase
    {
        private readonly Func<Scene, T> func;
        private readonly int estimatedUploadBytes;
        private readonly int estimatedMeshAdds;
        private readonly int estimatedInstanceAdds;
        public T Result { get; private set; } = default!;

        public SceneFuncCommand(
            long commandId,
            Func<Scene, T> func,
            bool requireGpuSync,
            int estimatedUploadBytes,
            int estimatedMeshAdds,
            int estimatedInstanceAdds)
            : base(commandId, $"SceneFuncCommand<{typeof(T).Name}>", requireGpuSync)
        {
            this.func = func;
            this.estimatedUploadBytes = Math.Max(0, estimatedUploadBytes);
            this.estimatedMeshAdds = Math.Max(0, estimatedMeshAdds);
            this.estimatedInstanceAdds = Math.Max(0, estimatedInstanceAdds);
        }

        protected override void ExecuteCore(Scene scene) => Result = func(scene);
        public override int EstimatedUploadBytes => estimatedUploadBytes;
        public override int EstimatedMeshAdds => estimatedMeshAdds;
        public override int EstimatedInstanceAdds => estimatedInstanceAdds;
    }

    private sealed class FireAndForgetSceneActionCommand : ISceneCommand
    {
        private readonly long commandId;
        private readonly Action<Scene> action;
        private readonly bool requireGpuSync;
        private readonly int estimatedUploadBytes;
        private readonly int estimatedMeshAdds;
        private readonly int estimatedInstanceAdds;

        public FireAndForgetSceneActionCommand(
            long commandId,
            Action<Scene> action,
            bool requireGpuSync,
            int estimatedUploadBytes,
            int estimatedMeshAdds,
            int estimatedInstanceAdds)
        {
            this.commandId = commandId;
            this.action = action;
            this.requireGpuSync = requireGpuSync;
            this.estimatedUploadBytes = Math.Max(0, estimatedUploadBytes);
            this.estimatedMeshAdds = Math.Max(0, estimatedMeshAdds);
            this.estimatedInstanceAdds = Math.Max(0, estimatedInstanceAdds);
        }

        public void Execute(Renderer renderer)
        {
            var startTicks = Stopwatch.GetTimestamp();
            try
            {
                if (requireGpuSync)
                    renderer.WaitForGpuIdleUnsafe();
                action(renderer.Scene);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fire-and-forget scene command #{CommandId} failed.", commandId);
            }
            finally
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency;
                if (elapsedMs > 250d)
                {
                    Log.Warning(
                        "Slow fire-and-forget scene command #{CommandId} took {ElapsedMs:F1} ms (GpuSync={RequireGpuSync})",
                        commandId,
                        elapsedMs,
                        requireGpuSync);
                }
            }
        }

        public int EstimatedUploadBytes => estimatedUploadBytes;
        public int EstimatedMeshAdds => estimatedMeshAdds;
        public int EstimatedInstanceAdds => estimatedInstanceAdds;
    }
}
