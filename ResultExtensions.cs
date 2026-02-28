using System;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

public sealed class VulkanException : Exception
{
    public Result Result { get; }

    public VulkanException(Result result)
        : base($"Unexpected Vulkan API error: {result}")
    {
        Result = result;
    }
}

public static class ResultExtensions
{
    public static void ThrowOnError(this Result result)
    {
        if (result != Result.Success)
            throw new VulkanException(result);
    }
}
