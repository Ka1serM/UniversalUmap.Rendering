using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Serilog;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace UniversalUmap.Rendering;

public sealed unsafe class Context : IDisposable
{
    public Vk Api { get; private set; }
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public Queue Queue { get; private set; }
    public uint QueueFamilyIndex { get; private set; }
    public CommandBufferPool Pool { get; private set; }
    public bool RayTracingSupported { get; private set; }
    public string DeviceName { get; private set; } = string.Empty;

    public Context(ICompositionGpuInterop gpuInterop)
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

        var validationEnabled = IsValidationEnabledInThisBuild();
        var availableLayers = EnumerateInstanceLayers(api);
        var enabledLayers = new List<string>();
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

        if (api.TryGetInstanceExtension(default(Instance), out ExtDebugUtils _) &&
            (validationEnabled || Environment.GetEnvironmentVariable("UVUMAP_ENABLE_DEBUG_UTILS") == "1"))
            instanceExtensions.Add("VK_EXT_debug_utils");

        using var pInstanceExtensions = new ByteStringList(instanceExtensions);
        using var pLayers = new ByteStringList(enabledLayers);
        var instanceInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = pInstanceExtensions.UCount,
            PpEnabledExtensionNames = pInstanceExtensions,
            EnabledLayerCount = pLayers.UCount,
            PpEnabledLayerNames = pLayers
        };

        api.CreateInstance(in instanceInfo, default, out var vkInstance).ThrowOnError();
        Device createdDevice = default;
        CommandBufferPool? createdPool = null;
        var success = false;

        try
        {
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
                    "VK_KHR_ray_tracing_pipeline",
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

                var rayTracingExtensionsPresent = rayTracingExtensions.All(x => api.IsDeviceExtensionPresent(physical, x));
                var rayTracingFeaturesSupported = false;

                if (rayTracingExtensionsPresent)
                {
                    var feature2 = new PhysicalDeviceFeatures2
                    {
                        SType = StructureType.PhysicalDeviceFeatures2
                    };
                    var descriptorIndexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
                    {
                        SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures
                    };
                    var accelerationStructureFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
                    {
                        SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr
                    };
                    var rayTracingPipelineFeatures = new PhysicalDeviceRayTracingPipelineFeaturesKHR
                    {
                        SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr
                    };
                    var bufferDeviceAddressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
                    {
                        SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures
                    };

                    feature2.PNext = &descriptorIndexingFeatures;
                    descriptorIndexingFeatures.PNext = &accelerationStructureFeatures;
                    accelerationStructureFeatures.PNext = &rayTracingPipelineFeatures;
                    rayTracingPipelineFeatures.PNext = &bufferDeviceAddressFeatures;
                    api.GetPhysicalDeviceFeatures2(physical, &feature2);

                    rayTracingFeaturesSupported =
                        descriptorIndexingFeatures.RuntimeDescriptorArray &&
                        accelerationStructureFeatures.AccelerationStructure &&
                        rayTracingPipelineFeatures.RayTracingPipeline &&
                        bufferDeviceAddressFeatures.BufferDeviceAddress;
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
                    var descriptorIndexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
                    {
                        SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures
                    };
                    var accelerationStructureFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
                    {
                        SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr
                    };
                    var rayTracingPipelineFeatures = new PhysicalDeviceRayTracingPipelineFeaturesKHR
                    {
                        SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr
                    };
                    var bufferDeviceAddressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
                    {
                        SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures
                    };

                    if (candidate.RayTracingEnabled)
                    {
                        descriptorIndexingFeatures.RuntimeDescriptorArray = true;
                        descriptorIndexingFeatures.ShaderSampledImageArrayNonUniformIndexing = true;
                        descriptorIndexingFeatures.DescriptorBindingPartiallyBound = true;

                        accelerationStructureFeatures.AccelerationStructure = true;
                        rayTracingPipelineFeatures.RayTracingPipeline = true;
                        bufferDeviceAddressFeatures.BufferDeviceAddress = true;

                        feature2.PNext = &descriptorIndexingFeatures;
                        descriptorIndexingFeatures.PNext = &accelerationStructureFeatures;
                        accelerationStructureFeatures.PNext = &rayTracingPipelineFeatures;
                        rayTracingPipelineFeatures.PNext = &bufferDeviceAddressFeatures;

                        deviceInfo.PNext = &feature2;
                    }
                    else
                    {
                        deviceInfo.PEnabledFeatures = &features;
                    }

                    api.CreateDevice(candidate.PhysicalDevice, in deviceInfo, default, out createdDevice).ThrowOnError();
                    api.GetDeviceQueue(createdDevice, candidate.QueueFamilyIndex, 0, out var queue);
                    createdPool = new CommandBufferPool(api, createdDevice, queue, candidate.QueueFamilyIndex);
                    success = true;

                    Api = api;
                    Instance = vkInstance;
                    PhysicalDevice = candidate.PhysicalDevice;
                    Device = createdDevice;
                    Queue = queue;
                    QueueFamilyIndex = candidate.QueueFamilyIndex;
                    Pool = createdPool;
                    RayTracingSupported = candidate.RayTracingEnabled;
                    DeviceName = candidate.Name;
                    Log.Information(
                        "Picked GPU: {GpuName} {RayTracingStatus}",
                        candidate.Name,
                        candidate.RayTracingEnabled ? "(Ray Tracing Enabled)" : "(Ray Tracing Not Supported)");
                    return;
                }
                catch (Exception ex)
                {
                    lastCreateFailure = ex;
                    createdPool?.Dispose();
                    createdPool = null;
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
                createdPool?.Dispose();
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

    private static bool IsValidationEnabledInThisBuild()
    {
#if RELEASE
        return false;
#else
        return true;
#endif
    }

    private static HashSet<string> EnumerateInstanceLayers(Vk api)
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

    private static void ConfigureValidationLayerPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VK_LAYER_PATH")))
            return;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("VULKAN_SDK"),
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

                Environment.SetEnvironmentVariable("VK_LAYER_PATH", explicitLayers);
                Log.Information("Configured VK_LAYER_PATH for validation layers: {LayerPath}", explicitLayers);
                return;
            }
        }
    }

    public void Dispose()
    {
        TextureAsset.DisposeSharedStagingRing();
        Pool.Dispose();
        Api.DestroyDevice(Device, default);
        Api.DestroyInstance(Instance, default);
    }
}
