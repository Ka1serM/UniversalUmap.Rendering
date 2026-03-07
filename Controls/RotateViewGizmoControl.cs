using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UniversalUmap.Rendering.Controls;

public sealed class RotateViewGizmoControl : CapturingControlBase
{
    private const float SnapDurationSeconds = 0.32f;
    private const float DefaultArcballPivotDistance = 10f;
    private const float CenterFadeStrength = 50f / 255f;
    private const float CenterFadeRate = 11f;
    private const float NegativeLabelFadeRate = 10f;
    private const float HoverFadeRate = 14f;
    private const float TickDeltaClampSeconds = 0.1f;
    private const float AnimationEpsilon = 0.0005f;
    private const double AxisClickMoveThreshold = 6d;

    private const float GizmoFadeFactor = 0.34f;
    private const float GizmoLineWidth = 3.5f;
    private const float GizmoLineHoverWidthBoost = 1.35f;
    private const float GizmoOutlineWidth = 2f;
    private const float GizmoCircleRadius = 12f;
    private const float GizmoCircleHoverRadiusBoost = 1.6f;
    private const float GizmoLabelSize = 17f;
    private const float GizmoBigCircleRadius = 66f;
    private const float GizmoAxisLineLength = GizmoBigCircleRadius - GizmoCircleRadius;

    private static readonly Color LabelColor = Color.FromArgb(230, 14, 18, 24);
    private static readonly Color[] AxisColors =
    [
        Color.FromArgb(255, 233, 62, 85),
        Color.FromArgb(255, 140, 206, 40),
        Color.FromArgb(255, 49, 155, 249)
    ];

    private static readonly string[] AxisLabels = ["X", "-X", "Y", "-Y", "Z", "-Z"];

    private static readonly Vector3[] AxisDirections =
    [
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ
    ];

    private readonly record struct AxisHandle(int Id, int AxisIndex, float Depth, Point ScreenPosition);

    private readonly DispatcherTimer animationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly AxisHandle[] handles = new AxisHandle[6];
    private readonly int[] drawOrder = [0, 1, 2, 3, 4, 5];
    private readonly float[] axisHoverFade = new float[6];
    private readonly float[] negativeLabelFade = new float[6];

    private VulkanViewerControl? viewer;
    private Scene? observedScene;
    private TopLevel? observedTopLevel;
    private Point localPointer;
    private bool hasTrackedPointer;
    private Point pressedPointer;
    private Vector3 pressedAxisWorldDirection;
    private int hoveredAxisId = -1;
    private int pressedAxisId = -1;
    private bool hoveredCenter;
    private bool rotating;
    private bool orbitCaptureMode;

    private float centerFadeCurrent;
    private float centerFadeTarget;
    private bool snapAnimating;
    private float snapTimeSeconds;
    private Vector3 snapStartPosition;
    private Quaternion snapStartRotation;
    private Vector3 snapTargetPosition;
    private Quaternion snapTargetRotation;
    private double lastTickSeconds;
    public static readonly StyledProperty<VulkanViewerControl?> SourceProperty =
        AvaloniaProperty.Register<RotateViewGizmoControl, VulkanViewerControl?>(nameof(Source));

    public VulkanViewerControl? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public RotateViewGizmoControl()
    {
                var diameter = (GizmoBigCircleRadius * 2f) + 2f;
        Width = diameter;
        Height = diameter;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        // Keep the visual center anchored where it was when the control used 100x100 bounds.
        var edgeInset = 32d - ((diameter - 100d) * 0.5d);
        Margin = new Thickness(0, edgeInset, edgeInset, 0);
        IsHitTestVisible = true;

        animationTimer.Tick += (_, _) => OnAnimationTick();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TryGetActiveRenderer(out _);
        AttachTopLevel();
        EnsureAnimationRunning(resetClock: true);
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndCapture();
        DetachObservedScene();
        DetachTopLevel();
        animationTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (Source?.Scene is { } scene)
                AttachObservedScene(scene);
            else
                DetachObservedScene();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!TryGetActiveRenderer(out var activeRenderer))
            return;

        var center = GetCenter();
        var labelTypeface = new Typeface(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this));

        BuildHandles(activeRenderer.Scene.GetCameraViewSnapshot().Rotation, center, GizmoAxisLineLength, handles, drawOrder);

        if (hoveredCenter || rotating || centerFadeCurrent > 0.001f)
        {
            var fill = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(centerFadeCurrent, 0f, 1f) * 255f), 255, 255, 255));
            context.DrawEllipse(fill, null, center, GizmoBigCircleRadius, GizmoBigCircleRadius);
        }

        for (var i = 0; i < drawOrder.Length; i++)
        {
            var h = handles[drawOrder[i]];
            var hover = axisHoverFade[h.Id];
            var colorFactor = GizmoFadeFactor + ((1f - GizmoFadeFactor) * ((h.Depth + 1f) * 0.5f));
            var animatedRadius = GizmoCircleRadius + (GizmoCircleHoverRadiusBoost * hover);
            var animatedLineWidth = GizmoLineWidth + (GizmoLineHoverWidthBoost * hover);
            var lineColor = WithAlpha(AxisColors[h.AxisIndex], colorFactor);

            var fillColor = IsPrimaryAxisHandle(h.Id)
                ? LerpColor(lineColor, Colors.White, 0.08f * hover)
                : WithAlpha(Darken(AxisColors[h.AxisIndex], 0.2f), colorFactor * 0.92f);

            Pen? circlePen = IsPrimaryAxisHandle(h.Id)
                ? null
                : new Pen(
                    new SolidColorBrush(LerpColor(WithAlpha(AxisColors[h.AxisIndex], colorFactor * 0.95f), Colors.White, 0.12f * hover)),
                    GizmoOutlineWidth);

            var handlePos = h.ScreenPosition;
            var radialDir = new Avalonia.Vector(handlePos.X - center.X, handlePos.Y - center.Y);
            var radialLen = Math.Sqrt((radialDir.X * radialDir.X) + (radialDir.Y * radialDir.Y));
            if (radialLen > 0.000001d)
                radialDir = new Avalonia.Vector(radialDir.X / radialLen, radialDir.Y / radialLen);
            else
                radialDir = default;

            var lineEnd = new Point(handlePos.X - radialDir.X * animatedRadius, handlePos.Y - radialDir.Y * animatedRadius);
            if (IsPrimaryAxisHandle(h.Id))
                context.DrawLine(new Pen(new SolidColorBrush(lineColor), animatedLineWidth), center, lineEnd);

            context.DrawEllipse(new SolidColorBrush(fillColor), circlePen, handlePos, animatedRadius, animatedRadius);

            var isPrimary = IsPrimaryAxisHandle(h.Id);
            var labelAlpha = isPrimary ? 1f : negativeLabelFade[h.Id];
            if (!isPrimary && labelAlpha <= 0.01f)
                continue;

            var hoverWhiten = 0.85f * hover;
            var baseLabelColor = WithAlpha(LabelColor, labelAlpha);
            var labelColor = LerpColor(baseLabelColor, Colors.White, hoverWhiten);
            var text = new FormattedText(
                AxisLabels[h.Id],
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                labelTypeface,
                GizmoLabelSize,
                new SolidColorBrush(labelColor));

            context.DrawText(text, new Point(handlePos.X - text.WidthIncludingTrailingWhitespace * 0.5, handlePos.Y - text.Height * 0.5));
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        localPointer = e.GetPosition(this);
        hasTrackedPointer = true;
        RecomputeHover(localPointer, startAnimations: true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (IsCaptureActive)
            return;

        ClearHoverState();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
            return;

        localPointer = e.GetPosition(this);
        hasTrackedPointer = true;
        RecomputeHover(localPointer, startAnimations: true);

        if (hoveredCenter)
        {
            if (TryGetActiveRenderer(out var activeRenderer))
                activeRenderer.Scene.SetArcballPivot(GetDefaultArcballPivot(activeRenderer));

            snapAnimating = false;
            rotating = true;
            orbitCaptureMode = true;
            BeginCapture(e.Pointer, localPointer);
            centerFadeTarget = CenterFadeStrength;
            EnsureAnimationRunning(resetClock: true);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (hoveredAxisId >= 0)
        {
            pressedAxisId = hoveredAxisId;
            pressedPointer = localPointer;
            pressedAxisWorldDirection = AxisDirections[pressedAxisId];
            orbitCaptureMode = false;
            BeginCapture(e.Pointer, localPointer);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        localPointer = e.GetPosition(this);
        hasTrackedPointer = true;

        if (!IsCaptureActive)
        {
            RecomputeHover(localPointer, startAnimations: true);
            return;
        }

        if (TryConsumeCaptureWarpMove())
        {
            e.Handled = true;
            return;
        }

        if (!TryGetActiveRenderer(out var activeRenderer))
            return;

        if (!orbitCaptureMode)
        {
            e.Handled = true;
            return;
        }

        var delta = GetCaptureDelta(localPointer);
        if (Math.Abs(delta.X) > double.Epsilon || Math.Abs(delta.Y) > double.Epsilon)
        {
            activeRenderer.Scene.OrbitAroundPivot((float)(-delta.X * 0.01), (float)(-delta.Y * 0.01));

            EnsureAnimationRunning();
            InvalidateVisual();
        }

        TryWrapCapture(localPointer);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (IsCaptureActive)
        {
            EndCapture(e.Pointer);
            var wasOrbitCapture = orbitCaptureMode;
            orbitCaptureMode = false;
            rotating = false;
            localPointer = e.GetPosition(this);
            hasTrackedPointer = true;
            if (wasOrbitCapture)
            {
                if (IsPointerOver)
                    RecomputeHover(localPointer, startAnimations: true);
                else
                    ClearHoverState();

                e.Handled = true;
                return;
            }
        }

        if (pressedAxisId >= 0)
        {
            var releasePointer = e.GetPosition(this);
            localPointer = releasePointer;
            hasTrackedPointer = true;
            RecomputeHover(localPointer, startAnimations: true);

            var dx = releasePointer.X - pressedPointer.X;
            var dy = releasePointer.Y - pressedPointer.Y;
            var clickDistanceSq = (dx * dx) + (dy * dy);
            if (clickDistanceSq <= AxisClickMoveThreshold * AxisClickMoveThreshold &&
                TryGetActiveRenderer(out var activeRenderer))
                StartSnapToAxis(activeRenderer, pressedAxisWorldDirection);

            pressedAxisId = -1;
            e.Handled = true;
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        EndCapture();
        orbitCaptureMode = false;
        rotating = false;
        pressedAxisId = -1;
        ClearHoverState();
    }

    private void OnAnimationTick()
    {
        if (this.GetVisualRoot() is null)
        {
            animationTimer.Stop();
            return;
        }

        if (!IsCaptureActive && hasTrackedPointer)
            RecomputeHover(localPointer, startAnimations: false);

        var deltaSeconds = GetTickDeltaSeconds();
        var changed = AdvanceAnimations(deltaSeconds);

        if (changed || rotating || IsCaptureActive)
            InvalidateVisual();
    }

    private bool NeedsAnimation()
    {
        if (rotating || IsCaptureActive || snapAnimating)
            return true;

        if (Math.Abs(centerFadeCurrent - centerFadeTarget) > AnimationEpsilon)
            return true;

        for (var i = 0; i < axisHoverFade.Length; i++)
        {
            var hoverTarget = hoveredAxisId == i ? 1f : 0f;
            if (Math.Abs(axisHoverFade[i] - hoverTarget) > AnimationEpsilon)
                return true;
        }

        for (var i = 1; i < negativeLabelFade.Length; i += 2)
        {
            var negativeTarget = hoveredAxisId == i ? 1f : 0f;
            if (Math.Abs(negativeLabelFade[i] - negativeTarget) > AnimationEpsilon)
                return true;
        }

        return false;
    }

    private void RecomputeHover(Point pointerPosition, bool startAnimations)
    {
        if (!TryGetActiveRenderer(out var activeRenderer))
            return;

        BuildHandles(activeRenderer.Scene.GetCameraViewSnapshot().Rotation, GetCenter(), GizmoAxisLineLength, handles, drawOrder);

        var previousHoveredAxis = hoveredAxisId;
        var previousHoveredCenter = hoveredCenter;

        hoveredAxisId = -1;
        hoveredCenter = false;

        var center = GetCenter();
        var dx = pointerPosition.X - center.X;
        var dy = pointerPosition.Y - center.Y;
        var centerDistanceSq = (dx * dx) + (dy * dy);
        if (centerDistanceSq <= GizmoBigCircleRadius * GizmoBigCircleRadius)
            hoveredCenter = true;

        var axisRadiusSq = GizmoCircleRadius * GizmoCircleRadius;
        for (var i = 0; i < handles.Length; i++)
        {
            var h = handles[i];
            var hx = pointerPosition.X - h.ScreenPosition.X;
            var hy = pointerPosition.Y - h.ScreenPosition.Y;
            if ((hx * hx) + (hy * hy) <= axisRadiusSq)
                hoveredAxisId = h.Id;
        }

        centerFadeTarget = (hoveredCenter || rotating) ? CenterFadeStrength : 0f;

        var hoverChanged = previousHoveredAxis != hoveredAxisId || previousHoveredCenter != hoveredCenter;
        if (startAnimations && (hoverChanged || NeedsAnimation()))
            EnsureAnimationRunning();

        if (hoverChanged)
            InvalidateVisual();
    }

    private void ClearHoverState()
    {
        hoveredAxisId = -1;
        hoveredCenter = false;
        centerFadeTarget = 0f;
        EnsureAnimationRunning();
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndCapture();
        orbitCaptureMode = false;
        rotating = false;
        pressedAxisId = -1;
        if (!IsCaptureActive && hasTrackedPointer)
            RecomputeHover(localPointer, startAnimations: false);
        InvalidateVisual();
    }

    private bool AdvanceAnimations(float deltaSeconds)
    {
        var changed = false;
        changed |= AnimateCenterFade(deltaSeconds);
        changed |= AnimateSnap(deltaSeconds);
        changed |= AnimateNegativeLabels(deltaSeconds);
        changed |= AnimateAxisHover(deltaSeconds);
        return changed;
    }

    private bool AnimateCenterFade(float deltaSeconds)
    {
        var next = StepToward(centerFadeCurrent, centerFadeTarget, CenterFadeRate, deltaSeconds);
        if (Math.Abs(next - centerFadeCurrent) <= AnimationEpsilon)
        {
            centerFadeCurrent = centerFadeTarget;
            return false;
        }

        centerFadeCurrent = next;
        return true;
    }

    private bool AnimateSnap(float deltaSeconds)
    {
        if (!snapAnimating || !TryGetActiveRenderer(out var activeRenderer))
            return false;

        var scene = activeRenderer.Scene;
        if (scene is null)
            return false;

        snapTimeSeconds += deltaSeconds;
        var t = Math.Clamp(snapTimeSeconds / SnapDurationSeconds, 0f, 1f);
        var eased = 1f - MathF.Pow(1f - t, 3f);
        var position = Vector3.Lerp(snapStartPosition, snapTargetPosition, eased);
        var rotation = Quaternion.Slerp(snapStartRotation, snapTargetRotation, eased);

        scene.SetCameraView(position, rotation);

        if (t >= 1f)
            snapAnimating = false;

        return true;
    }

    private bool AnimateNegativeLabels(float deltaSeconds)
    {
        var changed = false;
        for (var i = 1; i < negativeLabelFade.Length; i += 2)
        {
            var target = hoveredAxisId == i ? 1f : 0f;
            var next = StepToward(negativeLabelFade[i], target, NegativeLabelFadeRate, deltaSeconds);
            if (Math.Abs(next - negativeLabelFade[i]) <= AnimationEpsilon)
            {
                negativeLabelFade[i] = target;
                continue;
            }

            negativeLabelFade[i] = next;
            changed = true;
        }

        return changed;
    }

    private bool AnimateAxisHover(float deltaSeconds)
    {
        var changed = false;
        for (var i = 0; i < axisHoverFade.Length; i++)
        {
            var target = hoveredAxisId == i ? 1f : 0f;
            var next = StepToward(axisHoverFade[i], target, HoverFadeRate, deltaSeconds);
            if (Math.Abs(next - axisHoverFade[i]) <= AnimationEpsilon)
            {
                axisHoverFade[i] = target;
                continue;
            }

            axisHoverFade[i] = next;
            changed = true;
        }

        return changed;
    }

    private void EnsureAnimationRunning(bool resetClock = false)
    {
        if (resetClock || !animationTimer.IsEnabled)
            lastTickSeconds = 0d;

        if (!animationTimer.IsEnabled)
            animationTimer.Start();
    }

    private float GetTickDeltaSeconds()
    {
        var nowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (lastTickSeconds <= 0d)
        {
            lastTickSeconds = nowSeconds;
            return 0f;
        }

        var deltaSeconds = Math.Clamp(nowSeconds - lastTickSeconds, 0d, TickDeltaClampSeconds);
        lastTickSeconds = nowSeconds;
        return (float)deltaSeconds;
    }

    private void StartSnapToAxis(VulkanViewerControl activeViewer, Vector3 axisDirectionFromPivot)
    {
        if (axisDirectionFromPivot.LengthSquared() < 0.000001f)
            return;

        if (activeViewer.Scene is null)
            return;

        activeViewer.Scene.SetArcballPivot(GetDefaultArcballPivot(activeViewer));

        var cameraView = activeViewer.Scene.GetCameraViewSnapshot();
        var pivot = cameraView.ArcballPivot;
        var currentPosition = cameraView.Position;
        var currentRotation = cameraView.Rotation;
        var distance = Math.Max(1f, Vector3.Distance(currentPosition, pivot));
        var targetDirection = Vector3.Normalize(axisDirectionFromPivot);
        var targetPosition = pivot + targetDirection * distance;

        var currentDirection = currentPosition - pivot;
        if (currentDirection.LengthSquared() < 0.000001f)
            currentDirection = -targetDirection;
        else
            currentDirection = Vector3.Normalize(currentDirection);

        var deltaRotation = FromToRotation(currentDirection, targetDirection);
        var targetRotation = Quaternion.Normalize(deltaRotation * currentRotation);

        snapStartPosition = currentPosition;
        snapStartRotation = currentRotation;
        snapTargetPosition = targetPosition;
        snapTargetRotation = targetRotation;
        snapTimeSeconds = 0f;
        snapAnimating = true;

        EnsureAnimationRunning(resetClock: true);
        InvalidateVisual();
    }

    private bool TryGetActiveRenderer(out VulkanViewerControl activeViewer)
    {
        var resolved = Source;
        if (resolved is not null && resolved.Scene is not null)
        {
            if (!ReferenceEquals(viewer, resolved))
                viewer = resolved;

            AttachObservedScene(resolved.Scene);
            activeViewer = resolved;
            return true;
        }

        DetachObservedScene();
        viewer = null;
        activeViewer = null!;
        return false;
    }

    private void AttachObservedScene(Scene scene)
    {
        if (ReferenceEquals(observedScene, scene))
            return;

        DetachObservedScene();
        observedScene = scene;
        observedScene.CameraChanged += OnObservedSceneCameraChanged;
    }

    private void DetachObservedScene()
    {
        if (observedScene is null)
            return;

        observedScene.CameraChanged -= OnObservedSceneCameraChanged;
        observedScene = null;
    }

    private void OnObservedSceneCameraChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.GetVisualRoot() is null)
                return;

            if (IsPointerOver && !IsCaptureActive)
                RecomputeHover(localPointer, startAnimations: true);
            else
                InvalidateVisual();

            EnsureAnimationRunning();
        }, DispatcherPriority.Render);
    }

    private void AttachTopLevel()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (ReferenceEquals(observedTopLevel, topLevel))
            return;

        DetachTopLevel();
        observedTopLevel = topLevel;
        if (observedTopLevel is not null)
            observedTopLevel.PointerMoved += OnTopLevelPointerMoved;
    }

    private void DetachTopLevel()
    {
        if (observedTopLevel is null)
            return;

        observedTopLevel.PointerMoved -= OnTopLevelPointerMoved;
        observedTopLevel = null;
    }

    private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (this.GetVisualRoot() is null || IsCaptureActive)
            return;

        localPointer = e.GetPosition(this);
        hasTrackedPointer = true;
    }

    private Point GetCenter() => new(Bounds.Width * 0.5, Bounds.Height * 0.5);

    private static void BuildHandles(Quaternion cameraRotation, Point center, double axisLength, AxisHandle[] targetHandles, int[] targetDrawOrder)
    {
        var inverse = Quaternion.Inverse(cameraRotation);
        for (var i = 0; i < AxisDirections.Length; i++)
        {
            var axisIndex = i / 2;
            var viewDir = Vector3.Transform(AxisDirections[i], inverse);
            var screen = new Point(center.X - viewDir.X * axisLength, center.Y - viewDir.Y * axisLength);
            targetHandles[i] = new AxisHandle(i, axisIndex, viewDir.Z, screen);
            targetDrawOrder[i] = i;
        }

        for (var i = 0; i < targetDrawOrder.Length - 1; i++)
        {
            for (var j = i + 1; j < targetDrawOrder.Length; j++)
            {
                var left = targetDrawOrder[i];
                var right = targetDrawOrder[j];
                if (targetHandles[left].Depth <= targetHandles[right].Depth)
                    continue;

                targetDrawOrder[i] = right;
                targetDrawOrder[j] = left;
            }
        }
    }

    private static Vector3 GetDefaultArcballPivot(VulkanViewerControl activeViewer)
    {
        var scene = activeViewer.Scene!;
        var forward = Vector3.Transform(Vector3.UnitZ, scene.GetCameraViewSnapshot().Rotation);
        if (forward.LengthSquared() < 0.000001f)
            forward = Vector3.UnitZ;
        else
            forward = Vector3.Normalize(forward);

        return scene.GetCameraViewSnapshot().Position + (forward * DefaultArcballPivotDistance);
    }

    private static Quaternion FromToRotation(Vector3 from, Vector3 to)
    {
        var fromNorm = Vector3.Normalize(from);
        var toNorm = Vector3.Normalize(to);
        var dot = Math.Clamp(Vector3.Dot(fromNorm, toNorm), -1f, 1f);

        if (dot > 0.9999f)
            return Quaternion.Identity;

        if (dot < -0.9999f)
        {
            var axis = Vector3.Cross(fromNorm, Vector3.UnitY);
            if (axis.LengthSquared() < 0.000001f)
                axis = Vector3.Cross(fromNorm, Vector3.UnitX);

            axis = Vector3.Normalize(axis);
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }

        var rotationAxis = Vector3.Normalize(Vector3.Cross(fromNorm, toNorm));
        var angle = MathF.Acos(dot);
        return Quaternion.CreateFromAxisAngle(rotationAxis, angle);
    }

    private static float StepToward(float value, float target, float rate, float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
            return value;

        var t = 1f - MathF.Exp(-rate * deltaSeconds);
        return value + ((target - value) * t);
    }

    private static bool IsPrimaryAxisHandle(int id) => (id & 1) == 0;

    private static Color WithAlpha(Color c, float alphaScale)
    {
        var a = (byte)(c.A * Math.Clamp(alphaScale, 0f, 1f));
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    private static Color Darken(Color c, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        return Color.FromArgb(c.A, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
    }

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var alpha = (byte)(a.A + ((b.A - a.A) * t));
        var red = (byte)(a.R + ((b.R - a.R) * t));
        var green = (byte)(a.G + ((b.G - a.G) * t));
        var blue = (byte)(a.B + ((b.B - a.B) * t));
        return Color.FromArgb(alpha, red, green, blue);
    }
}
