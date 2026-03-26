using System;
using Avalonia;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.Vulkan;

internal sealed class GpuScenePicker
{
    private readonly Scene scene;
    public IGpuRenderPath? RenderPath { get; set; }

    public GpuScenePicker(Scene scene, IGpuRenderPath renderPath)
    {
        this.scene = scene;
        RenderPath = renderPath;
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
        instanceId = ShaderDefines.INVALID_INSTANCE;
        pixelX = 0;
        pixelY = 0;

        var renderPath = RenderPath;
        if (renderPath is null)
            return false;

        var renderSize = renderPath.RenderImageSize;
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

        if (!renderPath.QueryPixelUInt(renderPath.OutputCrypto, queryPixelX, queryPixelY, out var pickedId) ||
            !scene.TrySelectInstance(pickedId, out instance))
        {
            instanceId = ShaderDefines.INVALID_INSTANCE;
            return false;
        }

        instanceId = pickedId;
        return true;
    }
}
