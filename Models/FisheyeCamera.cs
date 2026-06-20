using System;
using System.Numerics;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Scenes;

public sealed class FisheyeCamera : CameraBase
{
    public override CameraProjectionType Projection => CameraProjectionType.Fisheye;

    internal FisheyeCamera(Input input, CameraSettings settings) : base(input, settings)
    {
    }

    protected override void ComputeProjectionData(
        ref CameraDataGpu next, Vector3 direction, Vector3 up, Vector3 right,
        int width, int height, float aspectRatio)
    {
        next.Horizontal = right;
        next.Vertical = up;
        next.FocalLength = Settings.FocalLengthMm * 0.001f;
        next.OrthoHeight = 0f;
        next.FisheyeFov = Math.Clamp(Settings.FieldOfView, 1f, 179f) * (MathF.PI / 180f);
        ApplyDepthOfField(ref next, Settings.FocalLengthMm);
    }
}
