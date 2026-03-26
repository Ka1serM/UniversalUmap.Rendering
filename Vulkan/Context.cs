using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Serilog;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using AvaloniaCompositor = Avalonia.Rendering.Composition.Compositor;

namespace UniversalUmap.Rendering.Vulkan;

public sealed class Context : IDisposable
{
    private static readonly object SharedSync = new();
    private static Context? shared;
    private static readonly bool VulkanDebugEnabled = GetEnvironmentFlag("UVUMAP_VULKAN_DEBUG");
    private static readonly PfnDebugUtilsMessengerCallbackEXT DebugMessengerCallbackDelegate = CreateDebugMessengerCallbackDelegate();
    private readonly List<CommandBuffer> usedCommandBuffers = [];
    private readonly object sync = new();
    private readonly ConcurrentDictionary<int, ThreadCommandPool> threadCommandPools = new();
    private DescriptorPool descriptorPool;
    private ExtDebugUtils? debugUtils;
    private DebugUtilsMessengerEXT debugMessenger;

    internal sealed class ThreadCommandPool
    {
        public required int ThreadId { get; init; }
        public required CommandPool Handle { get; init; }
    }

    public Vk Api { get; private set; }
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public Queue Queue { get; private set; }
    public uint QueueFamilyIndex { get; private set; }
    public bool RayTracingSupported { get; private set; }
    public string DeviceName { get; private set; } = string.Empty;
    public DescriptorPool DescriptorPool => descriptorPool;

    public CommandBuffer CreateCommandBuffer()
    {
        ReclaimCompletedCommandBuffers();
        return new CommandBuffer(this);
    }

    public void BeginCommandBuffer(CommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        commandBuffer.BeginRecording();
    }

    public void EndCommandBuffer(CommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        commandBuffer.EndRecording();
    }

    public void RetainForExecution(CommandBuffer commandBuffer, IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        ArgumentNullException.ThrowIfNull(resource);
        commandBuffer.Retain(resource);
    }

    public unsafe void SubmitCommandBuffer(
        CommandBuffer commandBuffer,
        ReadOnlySpan<Silk.NET.Vulkan.Semaphore> waitSemaphores = default,
        ReadOnlySpan<PipelineStageFlags> waitDstStageMask = default,
        ReadOnlySpan<Silk.NET.Vulkan.Semaphore> signalSemaphores = default)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        commandBuffer.EndRecording();

        if (!waitSemaphores.IsEmpty && !waitDstStageMask.IsEmpty && waitSemaphores.Length != waitDstStageMask.Length)
            throw new ArgumentException("waitDstStageMask length must match waitSemaphores length.");

        if (!waitSemaphores.IsEmpty && waitDstStageMask.IsEmpty)
        {
            Span<PipelineStageFlags> defaultWaitStages = stackalloc PipelineStageFlags[waitSemaphores.Length];
            for (var i = 0; i < defaultWaitStages.Length; i++)
                defaultWaitStages[i] = PipelineStageFlags.AllCommandsBit;

            fixed (Silk.NET.Vulkan.Semaphore* pWaitSemaphores = waitSemaphores, pSignalSemaphores = signalSemaphores)
            fixed (PipelineStageFlags* pWaitStages = defaultWaitStages)
            {
                SubmitCommandBufferInternal(commandBuffer, pWaitSemaphores, pWaitStages, pSignalSemaphores, waitSemaphores.Length, signalSemaphores.Length);
            }
        }
        else
        {
            fixed (Silk.NET.Vulkan.Semaphore* pWaitSemaphores = waitSemaphores, pSignalSemaphores = signalSemaphores)
            fixed (PipelineStageFlags* pWaitStages = waitDstStageMask)
            {
                SubmitCommandBufferInternal(commandBuffer, pWaitSemaphores, pWaitStages, pSignalSemaphores, waitSemaphores.Length, signalSemaphores.Length);
            }
        }

        MoveToUsed(commandBuffer);
    }

    public void SubmitAndWait(
        CommandBuffer commandBuffer,
        ReadOnlySpan<Silk.NET.Vulkan.Semaphore> waitSemaphores = default,
        ReadOnlySpan<PipelineStageFlags> waitDstStageMask = default,
        ReadOnlySpan<Silk.NET.Vulkan.Semaphore> signalSemaphores = default)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        SubmitCommandBuffer(commandBuffer, waitSemaphores, waitDstStageMask, signalSemaphores);
        WaitForCompletion(commandBuffer);
        ReclaimCompletedCommandBuffers();
    }

    public void WaitForCompletion(CommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        commandBuffer.WaitForCompletion();
    }

    public void WaitForSubmittedCommandBuffers()
    {
        FreeUsedCommandBuffers(waitForCompletion: true);
    }
    public static async Task<Context?> AcquireAsync(AvaloniaCompositor compositor)
    {
        lock (SharedSync)
        {
            if (shared is not null)
                return shared;
        }

        var interop = await compositor.TryGetCompositionGpuInterop();
        if (interop is null)
            return null;

        lock (SharedSync)
        {
            shared ??= new Context(interop);
            return shared;
        }
    }

    public static bool TryGetShared(out Context? context)
    {
        lock (SharedSync)
        {
            context = shared;
            return context is not null;
        }
    }

    public unsafe Context(ICompositionGpuInterop gpuInterop)
    {
        ConfigureValidationLayerPath();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!gpuInterop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle))
                throw new InvalidOperationException("Compositor backend does not support Vulkan Opaque NT image handles");
            if (!gpuInterop.SupportedSemaphoreTypes.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaqueNtHandle))
                throw new InvalidOperationException("Compositor backend does not support Vulkan Opaque NT semaphore handles");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (!gpuInterop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor))
                throw new InvalidOperationException("Compositor backend does not support Vulkan Opaque FD image handles");
            if (!gpuInterop.SupportedSemaphoreTypes.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor))
                throw new InvalidOperationException("Compositor backend does not support Vulkan Opaque FD semaphore handles");
        }
        else
        {
            throw new InvalidOperationException("Only Linux/Windows are supported in this interop path");
        }

        var api = GetApi();
        using var appName = new ByteString("UniversalUmap");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            ApiVersion = Vk.MakeVersion(1u, 1u, 0u),
            PEngineName = appName,
            EngineVersion = Vk.MakeVersion(1u, 0u, 0u),
            ApplicationVersion = Vk.MakeVersion(1u, 0u, 0u)
        };

        var instanceExtensions = new List<string>
        {
            "VK_KHR_get_physical_device_properties2",
            "VK_KHR_external_memory_capabilities",
            "VK_KHR_external_semaphore_capabilities"
        };

        var validationEnabled = IsValidationEnabled();
        var shaderPrintfRequested = GetEnvironmentFlag("UVUMAP_SHADER_PRINTF");
        var availableLayers = EnumerateInstanceLayers(api);
        var availableInstanceExtensions = EnumerateInstanceExtensions(api);
        var enabledLayers = new List<string>();
        var enabledValidationFeatures = new List<ValidationFeatureEnableEXT>();
        if (validationEnabled && availableLayers.Contains("VK_LAYER_KHRONOS_validation"))
        {
            enabledLayers.Add("VK_LAYER_KHRONOS_validation");
            Log.Information("Vulkan validation layer enabled: VK_LAYER_KHRONOS_validation");
        }
        else if (validationEnabled)
        {
            Log.Warning("Vulkan validation requested, but VK_LAYER_KHRONOS_validation was not found.");
            if (availableLayers.Count > 0)
                Log.Warning("Available Vulkan instance layers: {Layers}", string.Join(", ", availableLayers.OrderBy(x => x, StringComparer.Ordinal)));
        }

        var debugUtilsRequested = validationEnabled || GetEnvironmentFlag("UVUMAP_ENABLE_DEBUG_UTILS") || VulkanDebugEnabled;
        var validationFeaturesRequested = validationEnabled && shaderPrintfRequested;
        if (validationFeaturesRequested && availableInstanceExtensions.Contains("VK_EXT_validation_features"))
        {
            instanceExtensions.Add("VK_EXT_validation_features");
            enabledValidationFeatures.Add(ValidationFeatureEnableEXT.DebugPrintfExt);
        }
        else if (validationFeaturesRequested)
        {
            Log.Warning("Shader printf requested, but VK_EXT_validation_features is not available.");
        }

        if (api.TryGetInstanceExtension(default(Instance), out ExtDebugUtils _) && debugUtilsRequested)
            instanceExtensions.Add("VK_EXT_debug_utils");
        else if (debugUtilsRequested)
            Log.Warning("Vulkan debug utils requested, but VK_EXT_debug_utils is not available.");

        Log.Information(
            "Vulkan startup config: validationRequested={ValidationRequested}, shaderPrintfRequested={ShaderPrintfRequested}, debugUtilsRequested={DebugUtilsRequested}, vkLayerPath={LayerPath}",
            validationEnabled,
            shaderPrintfRequested,
            debugUtilsRequested,
            System.Environment.GetEnvironmentVariable("VK_LAYER_PATH") ?? "<unset>");
        Log.Information("Requested Vulkan instance extensions: {Extensions}", string.Join(", ", instanceExtensions.OrderBy(x => x, StringComparer.Ordinal)));
        Log.Information(
            "Requested Vulkan instance layers: {Layers}",
            enabledLayers.Count == 0 ? "<none>" : string.Join(", ", enabledLayers.OrderBy(x => x, StringComparer.Ordinal)));

        using var pInstanceExtensions = new ByteStringList(instanceExtensions);
        using var pLayers = new ByteStringList(enabledLayers);
        var validationFeatureArray = enabledValidationFeatures.Count == 0 ? null : enabledValidationFeatures.ToArray();
        var instanceInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = pInstanceExtensions.UCount,
            PpEnabledExtensionNames = pInstanceExtensions,
            EnabledLayerCount = pLayers.UCount,
            PpEnabledLayerNames = pLayers
        };
        Instance vkInstance = default;

        fixed (ValidationFeatureEnableEXT* pValidationFeatures = validationFeatureArray)
        {
            ValidationFeaturesEXT validationFeatures = default;
            if (validationFeatureArray is { Length: > 0 })
            {
                validationFeatures = new ValidationFeaturesEXT
                {
                    SType = StructureType.ValidationFeaturesExt,
                    EnabledValidationFeatureCount = (uint)validationFeatureArray.Length,
                    PEnabledValidationFeatures = pValidationFeatures
                };
                instanceInfo.PNext = &validationFeatures;
            }

            api.CreateInstance(in instanceInfo, default, out vkInstance).ThrowOnError();
        }

        Log.Information(
            "Vulkan instance created. ValidationLoaded={ValidationLoaded}, ShaderPrintfValidation={ShaderPrintfValidation}, DebugUtilsExtensionRequested={DebugUtilsExtensionRequested}",
            enabledLayers.Contains("VK_LAYER_KHRONOS_validation"),
            validationFeatureArray is { Length: > 0 },
            instanceExtensions.Contains("VK_EXT_debug_utils"));
        Device createdDevice = default;
        var success = false;

        try
        {
            if (debugUtilsRequested && api.TryGetInstanceExtension(vkInstance, out ExtDebugUtils createdDebugUtils))
            {
                debugUtils = createdDebugUtils;
                CreateDebugMessenger(vkInstance, createdDebugUtils);
            }

            uint physicalCount = 0;
            api.EnumeratePhysicalDevices(vkInstance, ref physicalCount, default).ThrowOnError();
            var devices = stackalloc PhysicalDevice[(int)physicalCount];
            api.EnumeratePhysicalDevices(vkInstance, ref physicalCount, devices).ThrowOnError();
            Log.Information("Available GPUs:");

            var hasInteropLuid = gpuInterop.DeviceLuid is { Length: > 0 };
            var hasInteropUuid = !hasInteropLuid && gpuInterop.DeviceUuid is { Length: > 0 };
            var candidates = new List<DeviceCandidate>();

            for (var i = 0; i < physicalCount; i++)
            {
                var physical = devices[i];
                var physicalId = new PhysicalDeviceIDProperties
                {
                    SType = StructureType.PhysicalDeviceIDProperties
                };
                var physicalProps2 = new PhysicalDeviceProperties2
                {
                    SType = StructureType.PhysicalDeviceProperties2,
                    PNext = &physicalId
                };
                api.GetPhysicalDeviceProperties2(physical, &physicalProps2);

                var interopMatch = true;
                if (hasInteropLuid)
                {
                    interopMatch = physicalId.DeviceLuidvalid &&
                                  new Span<byte>(physicalId.DeviceLuid, 8).SequenceEqual(gpuInterop.DeviceLuid);
                }
                else if (hasInteropUuid)
                {
                    interopMatch = new Span<byte>(physicalId.DeviceUuid, 16).SequenceEqual(gpuInterop.DeviceUuid);
                }

                var deviceExtensions = new List<string>
                {
                    "VK_KHR_external_memory",
                    "VK_KHR_external_semaphore"
                };
                var rayTracingExtensions = new[]
                {
                    "VK_KHR_buffer_device_address",
                    "VK_EXT_descriptor_indexing",
                    "VK_KHR_deferred_host_operations",
                    "VK_KHR_acceleration_structure",
                    "VK_KHR_ray_query",
                    "VK_KHR_spirv_1_4",
                    "VK_KHR_shader_float_controls"
                };
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    deviceExtensions.Add(KhrExternalMemoryWin32.ExtensionName);
                    deviceExtensions.Add(KhrExternalSemaphoreWin32.ExtensionName);
                    deviceExtensions.Add("VK_KHR_dedicated_allocation");
                    deviceExtensions.Add("VK_KHR_get_memory_requirements2");
                }
                else
                {
                    deviceExtensions.Add(KhrExternalMemoryFd.ExtensionName);
                    deviceExtensions.Add(KhrExternalSemaphoreFd.ExtensionName);
                }

                var hasRequiredExtensions = !deviceExtensions.Any(x => !api.IsDeviceExtensionPresent(physical, x));
                var name = Marshal.PtrToStringAnsi((IntPtr)physicalProps2.Properties.DeviceName) ?? "Unknown Vulkan device";
                var vramMiB = GetDeviceLocalMemoryBytes(api, physical) / (1024ul * 1024ul);
                Log.Information(
                    "{GpuName} (Type: {DeviceType}, VRAM: {VramMiB}MB, Extensions OK: {ExtensionsOk})",
                    name,
                    physicalProps2.Properties.DeviceType,
                    vramMiB,
                    hasRequiredExtensions ? "Yes" : "No");

                if (!hasRequiredExtensions)
                    continue;

                if (api.IsDeviceExtensionPresent(physical, "VK_KHR_dynamic_rendering"))
                    deviceExtensions.Add("VK_KHR_dynamic_rendering");
                if (api.IsDeviceExtensionPresent(physical, "VK_EXT_scalar_block_layout"))
                    deviceExtensions.Add("VK_EXT_scalar_block_layout");

                var rayTracingExtensionsPresent = rayTracingExtensions.All(x => api.IsDeviceExtensionPresent(physical, x));
                var rayTracingFeaturesSupported = false;
                var dynamicRenderingSupported = false;
                var scalarBlockLayoutSupported = false;

                {
                    var feature2 = new PhysicalDeviceFeatures2
                    {
                        SType = StructureType.PhysicalDeviceFeatures2
                    };
                    var dynamicRenderingFeatures = new PhysicalDeviceDynamicRenderingFeatures
                    {
                        SType = StructureType.PhysicalDeviceDynamicRenderingFeatures
                    };
                    var scalarBlockLayoutFeatures = new PhysicalDeviceScalarBlockLayoutFeatures
                    {
                        SType = StructureType.PhysicalDeviceScalarBlockLayoutFeatures
                    };
                    feature2.PNext = &dynamicRenderingFeatures;
                    dynamicRenderingFeatures.PNext = &scalarBlockLayoutFeatures;

                    if (rayTracingExtensionsPresent)
                    {
                        var descriptorIndexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
                        {
                            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures
                        };
                        var accelerationStructureFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
                        {
                            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr
                        };
                        var rayQueryFeatures = new PhysicalDeviceRayQueryFeaturesKHR
                        {
                            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr
                        };
                        var bufferDeviceAddressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
                        {
                            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures
                        };

                        scalarBlockLayoutFeatures.PNext = &descriptorIndexingFeatures;
                        descriptorIndexingFeatures.PNext = &accelerationStructureFeatures;
                        accelerationStructureFeatures.PNext = &rayQueryFeatures;
                        rayQueryFeatures.PNext = &bufferDeviceAddressFeatures;
                        api.GetPhysicalDeviceFeatures2(physical, &feature2);

                        rayTracingFeaturesSupported =
                            descriptorIndexingFeatures.RuntimeDescriptorArray &&
                            accelerationStructureFeatures.AccelerationStructure &&
                            rayQueryFeatures.RayQuery &&
                            bufferDeviceAddressFeatures.BufferDeviceAddress;
                    }
                    else
                    {
                        api.GetPhysicalDeviceFeatures2(physical, &feature2);
                    }

                    dynamicRenderingSupported = dynamicRenderingFeatures.DynamicRendering;
                    scalarBlockLayoutSupported = scalarBlockLayoutFeatures.ScalarBlockLayout;
                }

                if (!dynamicRenderingSupported)
                    continue;
                if (!scalarBlockLayoutSupported)
                {
                    Log.Information("Skipping GPU {GpuName}: scalarBlockLayout feature is not supported.", name);
                    continue;
                }

                if (rayTracingExtensionsPresent)
                {
                    // already queried above
                }

                var rayTracingEnabled = rayTracingExtensionsPresent && rayTracingFeaturesSupported;
                if (rayTracingEnabled)
                    deviceExtensions.AddRange(rayTracingExtensions);

                uint queueFamilyCount = 0;
                api.GetPhysicalDeviceQueueFamilyProperties(physical, ref queueFamilyCount, default);
                var queuePropsManaged = new QueueFamilyProperties[(int)queueFamilyCount];
                uint? graphicsQueueFamilyIndex = null;
                fixed (QueueFamilyProperties* queueProps = queuePropsManaged)
                {
                    api.GetPhysicalDeviceQueueFamilyProperties(physical, ref queueFamilyCount, queueProps);

                    for (uint queueFamilyIndex = 0; queueFamilyIndex < queueFamilyCount; queueFamilyIndex++)
                    {
                        if (!queueProps[queueFamilyIndex].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                            continue;

                        graphicsQueueFamilyIndex = queueFamilyIndex;
                        break;
                    }
                }

                if (graphicsQueueFamilyIndex is null)
                    continue;

                var candidate = new DeviceCandidate(
                    physical,
                    graphicsQueueFamilyIndex.Value,
                    deviceExtensions,
                    rayTracingEnabled,
                    interopMatch,
                    GetDeviceLocalMemoryBytes(api, physical),
                    physicalProps2.Properties.DeviceType,
                    name);
                candidates.Add(candidate);
            }

            if (candidates.Count == 0)
                throw new InvalidOperationException("No compatible Vulkan device/queue found");

            // Never pick software/CPU devices when at least one hardware GPU is available.
            var hasHardwareGpu = candidates.Any(c =>
                c.DeviceType is PhysicalDeviceType.DiscreteGpu or
                    PhysicalDeviceType.IntegratedGpu or
                    PhysicalDeviceType.VirtualGpu);
            var filteredCandidates = hasHardwareGpu
                ? candidates.Where(c => c.DeviceType != PhysicalDeviceType.Cpu).ToList()
                : candidates;
            var candidatesToTry = RankCandidates(filteredCandidates).ToList();

            if (candidatesToTry.Count == 0)
                throw new InvalidOperationException("No compatible Vulkan device/queue found");

            Exception? lastCreateFailure = null;
            foreach (var candidate in candidatesToTry)
            {
                try
                {
                    var queuePriority = 1f;
                    var queueInfo = new DeviceQueueCreateInfo
                    {
                        SType = StructureType.DeviceQueueCreateInfo,
                        QueueFamilyIndex = candidate.QueueFamilyIndex,
                        QueueCount = 1,
                        PQueuePriorities = &queuePriority
                    };

                    using var pDeviceExtensions = new ByteStringList(candidate.DeviceExtensions);
                    var deviceInfo = new DeviceCreateInfo
                    {
                        SType = StructureType.DeviceCreateInfo,
                        QueueCreateInfoCount = 1,
                        PQueueCreateInfos = &queueInfo,
                        EnabledExtensionCount = pDeviceExtensions.UCount,
                        PpEnabledExtensionNames = pDeviceExtensions
                    };

                    var features = new PhysicalDeviceFeatures();
                    var feature2 = new PhysicalDeviceFeatures2
                    {
                        SType = StructureType.PhysicalDeviceFeatures2,
                        Features = features
                    };
                    var dynamicRenderingFeatures = new PhysicalDeviceDynamicRenderingFeatures
                    {
                        SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
                        DynamicRendering = true
                    };
                    var scalarBlockLayoutFeatures = new PhysicalDeviceScalarBlockLayoutFeatures
                    {
                        SType = StructureType.PhysicalDeviceScalarBlockLayoutFeatures,
                        ScalarBlockLayout = true
                    };
                    var descriptorIndexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
                    {
                        SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures
                    };
                    var accelerationStructureFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
                    {
                        SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr
                    };
                    var rayQueryFeatures = new PhysicalDeviceRayQueryFeaturesKHR
                    {
                        SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr
                    };
                    var bufferDeviceAddressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
                    {
                        SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures
                    };

                    feature2.PNext = &dynamicRenderingFeatures;
                    dynamicRenderingFeatures.PNext = &scalarBlockLayoutFeatures;

                    if (candidate.RayTracingEnabled)
                    {
                        descriptorIndexingFeatures.RuntimeDescriptorArray = true;
                        descriptorIndexingFeatures.ShaderSampledImageArrayNonUniformIndexing = true;
                        descriptorIndexingFeatures.DescriptorBindingPartiallyBound = true;

                        accelerationStructureFeatures.AccelerationStructure = true;
                        rayQueryFeatures.RayQuery = true;
                        bufferDeviceAddressFeatures.BufferDeviceAddress = true;

                        scalarBlockLayoutFeatures.PNext = &descriptorIndexingFeatures;
                        descriptorIndexingFeatures.PNext = &accelerationStructureFeatures;
                        accelerationStructureFeatures.PNext = &rayQueryFeatures;
                        rayQueryFeatures.PNext = &bufferDeviceAddressFeatures;
                    }

                    deviceInfo.PNext = &feature2;

                    api.CreateDevice(candidate.PhysicalDevice, in deviceInfo, default, out createdDevice).ThrowOnError();
                    api.GetDeviceQueue(createdDevice, candidate.QueueFamilyIndex, 0, out var queue);

                    // Create a global descriptor pool large enough for all raytracing and compositing needs
                    var poolSizes = stackalloc DescriptorPoolSize[5];
                    poolSizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 10);
                    poolSizes[1] = new DescriptorPoolSize(DescriptorType.StorageImage, 30);
                    poolSizes[2] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 20_000); // 2 * MaxTextures
                    poolSizes[3] = new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, 2);
                    poolSizes[4] = new DescriptorPoolSize(DescriptorType.Sampler, 10);
                    var descriptorPoolInfo = new DescriptorPoolCreateInfo
                    {
                        SType = StructureType.DescriptorPoolCreateInfo,
                        Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit | DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                        MaxSets = 10,
                        PoolSizeCount = 5,
                        PPoolSizes = poolSizes
                    };
                    var createdDescriptorPool = default(DescriptorPool);
                    api.CreateDescriptorPool(createdDevice, in descriptorPoolInfo, default, out createdDescriptorPool).ThrowOnError();
                    
                    success = true;

                    Api = api;
                    Instance = vkInstance;
                    PhysicalDevice = candidate.PhysicalDevice;
                    Device = createdDevice;
                    Queue = queue;
                    QueueFamilyIndex = candidate.QueueFamilyIndex;
                    descriptorPool = createdDescriptorPool;
                    RayTracingSupported = candidate.RayTracingEnabled;
                    DeviceName = candidate.Name;
                    Log.Information(
                        "Picked GPU: {GpuName} {RayTracingStatus} QueueFamily={QueueFamily} Extensions=[{Extensions}]",
                        candidate.Name,
                        candidate.RayTracingEnabled ? "(Ray Tracing Enabled)" : "(Ray Tracing Not Supported)",
                        candidate.QueueFamilyIndex,
                        string.Join(", ", candidate.DeviceExtensions.OrderBy(x => x, StringComparer.Ordinal)));
                    return;
                }
                catch (Exception ex)
                {
                    lastCreateFailure = ex;
                    if (createdDevice.Handle != default)
                    {
                        api.DestroyDevice(createdDevice, default);
                        createdDevice = default;
                    }
                }
            }

            throw new InvalidOperationException("Failed to create Vulkan device from compatible GPU candidates", lastCreateFailure);
        }
        finally
        {
            if (!success)
            {
                if (createdDevice.Handle != default)
                    api.DestroyDevice(createdDevice, default);
                api.DestroyInstance(vkInstance, default);
            }
        }

    }

    private readonly record struct DeviceCandidate(
        PhysicalDevice PhysicalDevice,
        uint QueueFamilyIndex,
        List<string> DeviceExtensions,
        bool RayTracingEnabled,
        bool InteropMatch,
        ulong DeviceLocalMemoryBytes,
        PhysicalDeviceType DeviceType,
        string Name);

    private static int GetDeviceTypeScore(PhysicalDeviceType deviceType)
    {
        return deviceType switch
        {
            PhysicalDeviceType.DiscreteGpu => 5,
            PhysicalDeviceType.IntegratedGpu => 4,
            PhysicalDeviceType.VirtualGpu => 3,
            PhysicalDeviceType.Cpu => 1,
            _ => 1
        };
    }

    private static IOrderedEnumerable<DeviceCandidate> RankCandidates(IEnumerable<DeviceCandidate> candidates)
    {
        return candidates
            .OrderByDescending(c => GetDeviceTypeScore(c.DeviceType))
            .ThenByDescending(c => c.InteropMatch)
            .ThenByDescending(c => c.DeviceLocalMemoryBytes)
            .ThenByDescending(c => c.RayTracingEnabled);
    }

    private static ulong GetDeviceLocalMemoryBytes(Vk api, PhysicalDevice physicalDevice)
    {
        api.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memoryProperties);
        ulong total = 0;
        for (var i = 0; i < memoryProperties.MemoryHeapCount; i++)
        {
            if (memoryProperties.MemoryHeaps[i].Flags.HasFlag(MemoryHeapFlags.DeviceLocalBit))
                total += memoryProperties.MemoryHeaps[i].Size;
        }

        return total;
    }

    private static Vk GetApi()
    {
        return Vk.GetApi();
    }

    private static bool IsValidationEnabled()
    {
        if (GetEnvironmentFlag("UVUMAP_VULKAN_VALIDATION") || VulkanDebugEnabled)
            return true;

#if RELEASE
        return false;
#else
        return true;
#endif
    }

    private static bool GetEnvironmentFlag(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value is "1" or "true" or "TRUE" or "True" or "yes" or "YES" or "Yes" or "on" or "ON" or "On";
    }

    private static unsafe PfnDebugUtilsMessengerCallbackEXT CreateDebugMessengerCallbackDelegate()
        => new(new DebugUtilsMessengerCallbackFunctionEXT(DebugMessengerCallback));

    private unsafe void CreateDebugMessenger(Instance instance, ExtDebugUtils extDebugUtils)
    {
        var createInfo = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity =
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt |
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                DebugUtilsMessageSeverityFlagsEXT.InfoBitExt |
                DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt,
            MessageType =
                DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = DebugMessengerCallbackDelegate
        };

        extDebugUtils.CreateDebugUtilsMessenger(instance, in createInfo, default, out debugMessenger).ThrowOnError();
        Log.Information("Vulkan debug messenger enabled.");
    }

    private static unsafe uint DebugMessengerCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        void* userData)
    {
        var message = callbackData is null || callbackData->PMessage is null
            ? "<no message>"
            : Marshal.PtrToStringAnsi((nint)callbackData->PMessage) ?? "<no message>";
        var messageIdName = callbackData is null || callbackData->PMessageIdName is null
            ? "Unknown"
            : Marshal.PtrToStringAnsi((nint)callbackData->PMessageIdName) ?? "Unknown";
        var messageIdNumber = callbackData is null ? 0 : callbackData->MessageIdNumber;
        var objectSummary = callbackData is null ? string.Empty : BuildDebugObjectSummary(callbackData);
        var formatted = $"[{messageTypes}] {messageIdName} ({messageIdNumber}): {message}{objectSummary}";

        if ((messageSeverity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
            Log.Error("Vulkan validation: {Message}", formatted);
        else if ((messageSeverity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
            Log.Warning("Vulkan validation: {Message}", formatted);
        else
            Log.Information("Vulkan validation: {Message}", formatted);

        return Vk.False;
    }

    private static unsafe string BuildDebugObjectSummary(DebugUtilsMessengerCallbackDataEXT* callbackData)
    {
        if (callbackData->ObjectCount == 0 || callbackData->PObjects is null)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append(" | Objects: ");
        var count = Math.Min((int)callbackData->ObjectCount, 4);
        for (var i = 0; i < count; i++)
        {
            var debugObject = callbackData->PObjects[i];
            if (i > 0)
                builder.Append(", ");

            var name = debugObject.PObjectName is null
                ? "<unnamed>"
                : Marshal.PtrToStringAnsi((nint)debugObject.PObjectName) ?? "<unnamed>";
            builder.Append(debugObject.ObjectType);
            builder.Append('#');
            builder.Append(debugObject.ObjectHandle);
            builder.Append('(');
            builder.Append(name);
            builder.Append(')');
        }

        if (callbackData->ObjectCount > (uint)count)
            builder.Append(", ...");

        return builder.ToString();
    }

    private static unsafe HashSet<string> EnumerateInstanceLayers(Vk api)
    {
        uint layerCount = 0;
        api.EnumerateInstanceLayerProperties(ref layerCount, null).ThrowOnError();
        if (layerCount == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var layers = new LayerProperties[layerCount];
        fixed (LayerProperties* pLayers = layers)
            api.EnumerateInstanceLayerProperties(ref layerCount, pLayers).ThrowOnError();

        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < layerCount; i++)
        {
            string? name;
            fixed (byte* pLayerName = layers[i].LayerName)
                name = Marshal.PtrToStringAnsi((IntPtr)pLayerName);
            if (!string.IsNullOrWhiteSpace(name))
                result.Add(name);
        }
        return result;
    }

    private static unsafe HashSet<string> EnumerateInstanceExtensions(Vk api)
    {
        uint extensionCount = 0;
        api.EnumerateInstanceExtensionProperties((byte*)null, ref extensionCount, null).ThrowOnError();
        if (extensionCount == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var extensions = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* pExtensions = extensions)
            api.EnumerateInstanceExtensionProperties((byte*)null, ref extensionCount, pExtensions).ThrowOnError();

        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < extensionCount; i++)
        {
            string? name;
            fixed (byte* pExtensionName = extensions[i].ExtensionName)
                name = Marshal.PtrToStringAnsi((IntPtr)pExtensionName);
            if (!string.IsNullOrWhiteSpace(name))
                result.Add(name);
        }

        return result;
    }

    private static void ConfigureValidationLayerPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("VK_LAYER_PATH")))
            return;

        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            System.Environment.GetEnvironmentVariable("VULKAN_SDK"),
            "/home/marcel/Programs/Vulkan/1.4.335.0",
            "/var/home/marcel/Programs/Vulkan/1.4.335.0",
            Path.Combine(home, "Programs/Vulkan/1.4.335.0"),
            Path.Combine(home, "Programs", "Vulkan", "1.4.335.0")
        };

        foreach (var sdkPath in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var basePath = sdkPath!;
            var explicitLayerCandidates = new[]
            {
                Path.Combine(basePath, "share", "vulkan", "explicit_layer.d"),
                Path.Combine(basePath, "etc", "vulkan", "explicit_layer.d"),
                Path.Combine(basePath, "x86_64", "share", "vulkan", "explicit_layer.d"),
                Path.Combine(basePath, "x86_64", "etc", "vulkan", "explicit_layer.d")
            };

            foreach (var explicitLayers in explicitLayerCandidates.Distinct(StringComparer.Ordinal))
            {
                if (!Directory.Exists(explicitLayers))
                    continue;

                System.Environment.SetEnvironmentVariable("VK_LAYER_PATH", explicitLayers);
                Log.Information("Configured VK_LAYER_PATH for validation layers: {LayerPath}", explicitLayers);
                return;
            }
        }
    }

    public unsafe void Dispose()
    {
        TextureAsset.DisposeSharedStagingRing();
        WaitForSubmittedCommandBuffers();
        lock (sync)
        {
            if (descriptorPool.Handle != default)
                Api.DestroyDescriptorPool(Device, descriptorPool, default);
        }
        foreach (var threadCommandPool in threadCommandPools.Values)
            Api.DestroyCommandPool(Device, threadCommandPool.Handle, default);
        threadCommandPools.Clear();
        Api.DestroyDevice(Device, default);
        if (debugUtils is not null && debugMessenger.Handle != default && Instance.Handle != default)
            debugUtils.DestroyDebugUtilsMessenger(Instance, debugMessenger, default);
        Api.DestroyInstance(Instance, default);

        lock (SharedSync)
        {
            if (ReferenceEquals(shared, this))
                shared = null;
        }
    }

    private void ReclaimCompletedCommandBuffers()
        => FreeUsedCommandBuffers(waitForCompletion: false);

    private void FreeUsedCommandBuffers(bool waitForCompletion)
    {
        lock (sync)
        {
            for (var i = usedCommandBuffers.Count - 1; i >= 0; i--)
            {
                var commandBuffer = usedCommandBuffers[i];
                try
                {
                    if (commandBuffer.IsExternallyTracked)
                        continue;

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

    private unsafe Silk.NET.Vulkan.CommandBuffer AllocateCommandBuffer(ThreadCommandPool threadCommandPool)
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = threadCommandPool.Handle,
            CommandBufferCount = 1,
            Level = CommandBufferLevel.Primary
        };

        Api.AllocateCommandBuffers(Device, in allocateInfo, out Silk.NET.Vulkan.CommandBuffer commandBuffer);
        return commandBuffer;
    }

    private void MoveToUsed(CommandBuffer commandBuffer)
    {
        lock (sync)
            usedCommandBuffers.Add(commandBuffer);
    }

    private ThreadCommandPool GetOrCreateThreadCommandPool()
    {
        var threadId = Environment.CurrentManagedThreadId;
        return threadCommandPools.GetOrAdd(threadId, static (id, owner) => owner.CreateThreadCommandPool(id), this);
    }

    private unsafe ThreadCommandPool CreateThreadCommandPool(int threadId)
    {
        var commandPoolCreateInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = QueueFamilyIndex
        };

        lock (sync)
        {
            Api.CreateCommandPool(Device, in commandPoolCreateInfo, default, out var commandPool).ThrowOnError();
            return new ThreadCommandPool
            {
                ThreadId = threadId,
                Handle = commandPool
            };
        }
    }

    private unsafe void SubmitCommandBufferInternal(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Semaphore* pWaitSemaphores,
        PipelineStageFlags* pWaitStages,
        Silk.NET.Vulkan.Semaphore* pSignalSemaphores,
        int waitSemaphoreCount,
        int signalSemaphoreCount)
    {
        var cmd = commandBuffer.InternalHandle;
        var hasWaitSemaphores = waitSemaphoreCount > 0;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = hasWaitSemaphores ? (uint)waitSemaphoreCount : 0u,
            PWaitSemaphores = pWaitSemaphores,
            PWaitDstStageMask = hasWaitSemaphores ? pWaitStages : null,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = signalSemaphoreCount > 0 ? (uint)signalSemaphoreCount : 0u,
            PSignalSemaphores = pSignalSemaphores
        };

        var fenceValue = commandBuffer.Fence;
        lock (sync)
        {
            Api.ResetFences(Device, 1, in fenceValue).ThrowOnError();
            Api.QueueSubmit(Queue, 1, in submitInfo, fenceValue).ThrowOnError();
        }
    }

    public sealed class CommandBuffer : IDisposable
    {
        private List<IDisposable>? retainedResources;
        private bool started;
        private bool ended;
        private int externalReferenceCount;

        internal unsafe CommandBuffer(Context owner)
        {
            Context = owner;
            OriginCommandPool = owner.GetOrCreateThreadCommandPool();
            InternalHandle = owner.AllocateCommandBuffer(OriginCommandPool);

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit
            };

            owner.Api.CreateFence(owner.Device, in fenceInfo, default, out var fence).ThrowOnError();
            Fence = fence;
        }

        internal Context Context { get; }
        internal ThreadCommandPool OriginCommandPool { get; }
        public Silk.NET.Vulkan.CommandBuffer InternalHandle { get; }
        internal Fence Fence { get; }
        internal bool IsExternallyTracked => externalReferenceCount > 0;

        public IntPtr Handle => InternalHandle.Handle;

        internal void BeginRecording()
        {
            if (started)
                return;

            started = true;
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            Context.Api.BeginCommandBuffer(InternalHandle, in beginInfo).ThrowOnError();
        }

        internal void EndRecording()
        {
            if (!started || ended)
                return;

            ended = true;
            Context.Api.EndCommandBuffer(InternalHandle).ThrowOnError();
        }

        internal void Retain(IDisposable resource)
        {
            retainedResources ??= new List<IDisposable>(2);
            retainedResources.Add(resource);
        }

        internal void AddExternalReference()
        {
            externalReferenceCount++;
        }

        internal void ReleaseExternalReference()
        {
            if (externalReferenceCount == 0)
                throw new InvalidOperationException("Command buffer external reference count is already zero.");

            externalReferenceCount--;
        }

        internal bool IsExecutionComplete()
        {
            var result = Context.Api.GetFenceStatus(Context.Device, Fence);
            return result == Result.Success;
        }

        internal unsafe void WaitForCompletion()
        {
            var fence = Fence;
            Context.Api.WaitForFences(Context.Device, 1, in fence, true, ulong.MaxValue).ThrowOnError();
        }

        internal unsafe void Dispose(bool waitForCompletion)
        {
            try
            {
                if (waitForCompletion)
                {
                    var fence = Fence;
                    Context.Api.WaitForFences(Context.Device, 1, in fence, true, ulong.MaxValue).ThrowOnError();
                }
                else if (!IsExecutionComplete())
                    return;
            }
            finally
            {
                retainedResources?.ForEach(resource => resource.Dispose());
                retainedResources?.Clear();
                Context.Api.DestroyFence(Context.Device, Fence, default);

                var commandBuffer = InternalHandle;
                lock (Context.sync)
                    Context.Api.FreeCommandBuffers(Context.Device, OriginCommandPool.Handle, 1, in commandBuffer);
            }
        }

        public void Dispose()
        {
            Dispose(waitForCompletion: true);
        }
    }
}
