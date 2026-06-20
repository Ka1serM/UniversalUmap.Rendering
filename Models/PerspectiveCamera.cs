using System;
using System.Numerics;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Scenes;

public sealed class PerspectiveCamera : CameraBase
{
    public override CameraProjectionType Projection => CameraProjectionType.Perspective;

    internal PerspectiveCamera(Input input, CameraSettings settings) : base(input, settings)
    {
    }

    public override void FrameBoundingBox(Vector3 center, float radius)
    {
        var fovRad = Settings.FieldOfView * (MathF.PI / 180f);
        var tanHalfFov = MathF.Tan(fovRad * 0.5f);

        var distance = (radius * 1.5f) / Math.Max(tanHalfFov, 0.0001f);
        distance = Math.Max(distance, 0.01f);

        var forward = Vector3.Transform(LocalForward, Rotation);
        SetPosition(center - forward * distance);

        ArcballPivot = center;
        Settings.SetFocusDistanceSilent(distance);
    }

    protected override void ComputeProjectionData(
        ref CameraDataGpu next, Vector3 direction, Vector3 up, Vector3 right,
        int width, int height, float aspectRatio)
    {
        var fovRad = Settings.FieldOfView * (MathF.PI / 180f);
        var tanHalfFov = MathF.Tan(fovRad * 0.5f);
        var halfWidth = tanHalfFov;
        var halfHeight = halfWidth / aspectRatio;

        var focalLengthMm = CameraSettings.SensorWidthMm / (2f * tanHalfFov);

        next.Horizontal = right * (2f * halfWidth);
        next.Vertical = up * (2f * halfHeight);
        next.FocalLength = focalLengthMm * 0.001f;
        next.OrthoHeight = 0f;
        next.FisheyeFov = 0f;
        ApplyDepthOfField(ref next, focalLengthMm);
    }
}
