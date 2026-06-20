using System;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalUmap.Rendering.Inspector;

namespace UniversalUmap.Rendering.Scenes;

public sealed class CameraSettings : ObservableObject
{
    public const float SensorWidthMm = 32f;

    private float fieldOfView = 90f;
    private float focalLengthMm = FocalLengthForFov(90f);
    private float aperture;
    private float focusDistance = 4f;
    private float bokehBias = 1f;
    private bool syncing;

    public event Action? Changed;

    [Detail("Field of View", Group = "Lens", Order = 0, Min = 1, Max = 179, Unit = "°", Format = "0.0")]
    public float FieldOfView
    {
        get => fieldOfView;
        set
        {
            var clamped = Math.Clamp(value, 1f, 179f);
            if (!SetProperty(ref fieldOfView, clamped, nameof(FieldOfView)) || syncing)
                return;

            syncing = true;
            FocalLengthMm = FocalLengthForFov(clamped);
            syncing = false;
            Changed?.Invoke();
        }
    }

    [Detail("Focal Length", Group = "Lens", Order = 1, Min = 8, Max = 200, Unit = "mm", Format = "0.0")]
    public float FocalLengthMm
    {
        get => focalLengthMm;
        set
        {
            var clamped = Math.Max(0.001f, value);
            if (!SetProperty(ref focalLengthMm, clamped, nameof(FocalLengthMm)) || syncing)
                return;

            syncing = true;
            FieldOfView = FovForFocalLength(clamped);
            syncing = false;
            Changed?.Invoke();
        }
    }

    [Detail("F-Stop", Group = "Depth of Field", Order = 0, Min = 0, Max = 32, Format = "0.0")]
    public float Aperture
    {
        get => aperture;
        set
        {
            if (SetProperty(ref aperture, Math.Max(0f, value), nameof(Aperture)))
                Changed?.Invoke();
        }
    }

    [Detail("Focus Distance", Group = "Depth of Field", Order = 1, Min = 0.1, Max = 100, Unit = "m", Format = "0.0")]
    public float FocusDistance
    {
        get => focusDistance;
        set
        {
            if (SetProperty(ref focusDistance, Math.Max(0.001f, value), nameof(FocusDistance)))
                Changed?.Invoke();
        }
    }

    [Detail("Bokeh Bias", Group = "Depth of Field", Order = 2, Min = 0.1, Max = 4, Format = "0.00")]
    public float BokehBias
    {
        get => bokehBias;
        set
        {
            if (SetProperty(ref bokehBias, Math.Max(0.001f, value), nameof(BokehBias)))
                Changed?.Invoke();
        }
    }

    internal void SetFocusDistanceSilent(float value) => SetProperty(ref focusDistance, Math.Max(0.001f, value), nameof(FocusDistance));

    private static float FocalLengthForFov(float fovDegrees)
    {
        var halfAngle = fovDegrees * (MathF.PI / 180f) * 0.5f;
        return SensorWidthMm / (2f * MathF.Tan(halfAngle));
    }

    private static float FovForFocalLength(float focalLength)
    {
        var fov = 2f * MathF.Atan(SensorWidthMm / (2f * focalLength)) * (180f / MathF.PI);
        return Math.Clamp(fov, 1f, 179f);
    }
}
