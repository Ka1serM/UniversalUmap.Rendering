using System;
using Avalonia;

namespace UniversalUmap.Rendering;

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

        lock (scene.SyncRoot)
        {
            if (!raytracer.QueryPixelUInt(raytracer.OutputCrypto, pixelX, pixelY, out var pickedId) ||
                pickedId == SharedShaderDefines.InvalidInstance ||
                pickedId >= scene.MeshInstances.Count)
            {
                scene.ClearSelection();
                return false;
            }

            scene.SelectInstance((int)pickedId);
            instanceId = pickedId;
            instance = scene.MeshInstances[(int)pickedId];
            return true;
        }
    }
}
