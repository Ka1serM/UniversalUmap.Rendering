using System;
using Avalonia;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Raytracing;

namespace UniversalUmap.Rendering.Vulkan;

internal sealed class GpuScenePicker
{
    private readonly Scene scene;
    private readonly GpuRaytracer raytracer;

    public GpuScenePicker(Scene scene, GpuRaytracer raytracer)
    {
        this.scene = scene;
        this.raytracer = raytracer;
    }

    public bool TryPickInstance(
        Point localPosition,
        Size controlSize,
        out MeshInstance? instance,
        out uint instanceId,
        out int pixelX,
        out int pixelY)
    {
        instance = null;
        instanceId = SharedShaderDefines.InvalidInstance;
        pixelX = 0;
        pixelY = 0;

        var renderSize = raytracer.RenderImageSize;
        if (controlSize.Width <= 0 || controlSize.Height <= 0 || renderSize.Width <= 0 || renderSize.Height <= 0)
            return false;

        var maxX = renderSize.Width - 1;
        var maxY = renderSize.Height - 1;
        var scaledX = localPosition.X * renderSize.Width / controlSize.Width;
        var scaledY = localPosition.Y * renderSize.Height / controlSize.Height;
        pixelX = (int)Math.Floor(scaledX);
        pixelY = (int)Math.Floor(scaledY);

        if (pixelX < 0 || pixelY < 0 || pixelX > maxX || pixelY > maxY)
            return false;

        var queryPixelX = pixelX;
        var queryPixelY = pixelY;
        if (!raytracer.QueryPixelUInt(raytracer.OutputCrypto, queryPixelX, queryPixelY, out var pickedId) ||
            !scene.TrySelectInstance(pickedId, out instance))
        {
            instanceId = SharedShaderDefines.InvalidInstance;
            return false;
        }

        instanceId = pickedId;
        return true;
    }
}
