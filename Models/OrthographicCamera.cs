using System;
using System.Numerics;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Scenes;

public sealed class OrthographicCamera : CameraBase
{
    private const float ReferenceHeight = 10f;
    private const float ReferenceFocalLengthMm = 21f;

    public override CameraProjectionType Projection => CameraProjectionType.Orthographic;

    internal OrthographicCamera(Input input, CameraSettings settings) : base(input, settings)
    {
    }

    private float OrthoHeight => ReferenceHeight * ReferenceFocalLengthMm / Math.Max(0.001f, Settings.FocalLengthMm);

    public override void Dolly(float amount)
    {
        if (Math.Abs(amount) < 0.000001f)
            return;

        var scale = 1f + amount * 0.05f;
        Settings.FocalLengthMm = Math.Max(0.001f, Settings.FocalLengthMm / scale);
        MarkDirectInteraction();
        RebuildData();
    }

    public override void FrameBoundingBox(Vector3 center, float radius)
    {
        var targetHeight = radius * 3f;
        Settings.FocalLengthMm = ReferenceFocalLengthMm * ReferenceHeight / Math.Max(0.001f, targetHeight);

        var dir = Vector3.Transform(LocalForward, Rotation);
        SetPosition(center - dir * 5000f);

        ArcballPivot = center;
        Settings.SetFocusDistanceSilent(Vector3.Distance(Position, center));
    }

    protected override void ComputeProjectionData(
        ref CameraDataGpu next, Vector3 direction, Vector3 up, Vector3 right,
        int width, int height, float aspectRatio)
    {
        var orthoHeight = OrthoHeight;
        var halfHeight = orthoHeight * 0.5f;
        var halfWidth = halfHeight * aspectRatio;

        next.Horizontal = right * halfWidth;
        next.Vertical = up * halfHeight;
        next.FocalLength = Settings.FocalLengthMm * 0.001f;
        next.OrthoHeight = orthoHeight;
        next.FisheyeFov = 0f;
        ClearDepthOfField(ref next);
    }
}
