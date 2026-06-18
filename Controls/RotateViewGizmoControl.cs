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

public sealed class RotateViewGizmoControl : ContentControl
{
    private const float SnapDurationSeconds = 0.32f;
    private const float DefaultArcballPivotDistance = 10f;
    private const float CenterFadeStrength = 50f / 255f;
    private const float CenterFadeRate = 11f;
    private const float NegativeLabelFadeRate = 10f;
    private const float HoverFadeRate = 14f;
    private const float TickDeltaClampSeconds = 0.1f;
    private const float AnimationEpsilon = 0.0005f;

    private const float GizmoScale = 0.75f;

    private const double AxisClickMoveThreshold = 6d * GizmoScale;

    private const float GizmoFadeFactor = 1f;
    private const float GizmoLineWidth = 3.5f * GizmoScale;
    private const float GizmoLineHoverWidthBoost = 1.35f * GizmoScale;
    private const float GizmoOutlineWidth = 2f * GizmoScale;
    private const float GizmoCircleRadius = 12f * GizmoScale;
    private const float GizmoCircleHoverRadiusBoost = 1.6f * GizmoScale;
    private const float GizmoLabelSize = 17f * GizmoScale;

    private const float GizmoBigCircleRadius = 66f * GizmoScale;
    private const float GizmoDiameter = GizmoBigCircleRadius * 2f;
    private const float GizmoAxisLineLength = GizmoBigCircleRadius - GizmoCircleRadius;

    private static readonly Color LabelColor = Color.FromArgb(255, 14, 18, 24);

    private static readonly Color[] AxisColors =
    [
        Color.FromArgb(255, 233, 62, 85),
        Color.FromArgb(255, 140, 206, 40),
        Color.FromArgb(255, 49, 155, 249)
    ];

    private static readonly string[] AxisLabels = ["X", "-X", "Y", "-Y", "Z", "-Z"];

    private static readonly Vector3[] AxisDirections =
    [
        Vector3.UnitX,
        -Vector3.UnitX,
        Vector3.UnitY,
        -Vector3.UnitY,
        Vector3.UnitZ,
        -Vector3.UnitZ
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
    private Point pressedPointer;
    private Vector3 pressedAxisWorldDirection;

    private bool hasTrackedPointer;
    private bool hoveredCenter;
    private bool rotating;
    private bool orbitCaptureMode;
    private bool snapAnimating;

    private int hoveredAxisId = -1;
    private int pressedAxisId = -1;

    private float centerFadeCurrent;
    private float centerFadeTarget;
    private float snapTimeSeconds;

    private Vector3 snapStartPosition;
    private Vector3 snapTargetPosition;
    private Quaternion snapStartRotation;
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
        Width = GizmoDiameter;
        Height = GizmoDiameter;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        // Keep the visual center anchored where it was when the control used 100x100 bounds.
        var edgeInset = 32d - ((GizmoDiameter - 100d) * 0.5d);
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
        if (PointerCapture.IsOwnedBy(this))
            PointerCapture.End(this);

        DetachObservedScene();
        DetachTopLevel();
        animationTimer.Stop();

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != SourceProperty)
            return;

        if (Source?.Scene is { } scene)
            AttachObservedScene(scene);
        else
            DetachObservedScene();

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (!TryGetActiveRenderer(out var activeRenderer))
            return;

        var center = GetCenter();
        var scene = activeRenderer.Scene!;
        var cameraView = scene.GetCameraViewSnapshot();
        var labelTypeface = GetLabelTypeface();

        BuildHandles(cameraView.Rotation, center, GizmoAxisLineLength, handles, drawOrder);

        DrawCenterFade(context, center);

        foreach (var orderIndex in drawOrder)
            DrawHandle(context, handles[orderIndex], center, labelTypeface);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        TrackPointer(e);
        RecomputeHover(localPointer, startAnimations: true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (!PointerCapture.IsOwnedBy(this))
            ClearHoverState();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
            return;

        TrackPointer(e);
        RecomputeHover(localPointer, startAnimations: true);

        if (hoveredCenter)
        {
            BeginOrbitCapture(e);
            return;
        }

        if (hoveredAxisId >= 0)
            BeginAxisPress(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        TrackPointer(e);

        if (!PointerCapture.IsOwnedBy(this))
        {
            RecomputeHover(localPointer, startAnimations: true);
            return;
        }

        if (!orbitCaptureMode)
        {
            e.Handled = true;
            return;
        }

        if (!TryGetActiveRenderer(out var activeRenderer))
        {
            e.Handled = true;
            return;
        }

        var delta = PointerCapture.UpdateMove(this, localPointer);
        if (Math.Abs(delta.X) > double.Epsilon || Math.Abs(delta.Y) > double.Epsilon)
        {
            activeRenderer.Scene!.OrbitAroundPivot((float)(-delta.X * 0.01), (float)(-delta.Y * 0.01));
            EnsureAnimationRunning();
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (PointerCapture.IsOwnedBy(this))
        {
            PointerCapture.End(this, e.Pointer);

            var wasOrbitCapture = orbitCaptureMode;
            orbitCaptureMode = false;
            rotating = false;

            TrackPointer(e);

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
            CompleteAxisPress(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (PointerCapture.IsOwnedBy(this))
            PointerCapture.End(this);

        orbitCaptureMode = false;
        rotating = false;
        pressedAxisId = -1;

        if (hasTrackedPointer)
            RecomputeHover(localPointer, startAnimations: false);

        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        if (PointerCapture.IsOwnedBy(this))
            PointerCapture.End(this);

        orbitCaptureMode = false;
        rotating = false;
        pressedAxisId = -1;

        ClearHoverState();
    }

    private void DrawCenterFade(DrawingContext context, Point center)
    {
        if (!hoveredCenter && !rotating && centerFadeCurrent <= 0.001f)
            return;

        var alpha = (byte)(Math.Clamp(centerFadeCurrent, 0f, 1f) * 255f);
        var fill = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));

        context.DrawEllipse(fill, null, center, GizmoBigCircleRadius, GizmoBigCircleRadius);
    }

    private void DrawHandle(DrawingContext context, AxisHandle handle, Point center, Typeface labelTypeface)
    {
        var hover = axisHoverFade[handle.Id];
        var colorFactor = GizmoFadeFactor + ((1f - GizmoFadeFactor) * ((handle.Depth + 1f) * 0.5f));
        var lineColor = WithAlpha(AxisColors[handle.AxisIndex], colorFactor);

        var animatedRadius = GizmoCircleRadius + GizmoCircleHoverRadiusBoost * hover;
        var animatedLineWidth = GizmoLineWidth + GizmoLineHoverWidthBoost * hover;

        var isPrimary = IsPrimaryAxisHandle(handle.Id);
        var fillColor = isPrimary
            ? LerpColor(lineColor, Colors.White, 0.08f * hover)
            : WithAlpha(Darken(AxisColors[handle.AxisIndex], 0.2f), colorFactor * 0.92f);

        var circlePen = isPrimary
            ? null
            : new Pen(
                new SolidColorBrush(LerpColor(WithAlpha(AxisColors[handle.AxisIndex], colorFactor * 0.95f), Colors.White, 0.12f * hover)),
                GizmoOutlineWidth);

        if (isPrimary)
            DrawAxisLine(context, center, handle.ScreenPosition, animatedRadius, lineColor, animatedLineWidth);

        context.DrawEllipse(new SolidColorBrush(fillColor), circlePen, handle.ScreenPosition, animatedRadius, animatedRadius);
        DrawHandleLabel(context, handle, hover, isPrimary, labelTypeface);
    }

    private static void DrawAxisLine(
        DrawingContext context,
        Point center,
        Point handlePosition,
        float handleRadius,
        Color lineColor,
        float lineWidth)
    {
        var radialDir = new Avalonia.Vector(handlePosition.X - center.X, handlePosition.Y - center.Y);
        var radialLen = Math.Sqrt(radialDir.X * radialDir.X + radialDir.Y * radialDir.Y);

        if (radialLen > 0.000001d)
            radialDir = new Avalonia.Vector(radialDir.X / radialLen, radialDir.Y / radialLen);
        else
            radialDir = default;

        var lineEnd = new Point(
            handlePosition.X - radialDir.X * handleRadius,
            handlePosition.Y - radialDir.Y * handleRadius);

        context.DrawLine(new Pen(new SolidColorBrush(lineColor), lineWidth), center, lineEnd);
    }

    private void DrawHandleLabel(
        DrawingContext context,
        AxisHandle handle,
        float hover,
        bool isPrimary,
        Typeface labelTypeface)
    {
        var labelAlpha = isPrimary ? 1f : negativeLabelFade[handle.Id];
        if (!isPrimary && labelAlpha <= 0.01f)
            return;

        var baseLabelColor = WithAlpha(LabelColor, labelAlpha);
        var labelColor = LerpColor(baseLabelColor, Colors.White, 0.85f * hover);

        var text = new FormattedText(
            AxisLabels[handle.Id],
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            labelTypeface,
            GizmoLabelSize,
            new SolidColorBrush(labelColor));

        context.DrawText(
            text,
            new Point(
                handle.ScreenPosition.X - text.WidthIncludingTrailingWhitespace * 0.5,
                handle.ScreenPosition.Y - text.Height * 0.5));
    }

    private Typeface GetLabelTypeface() =>
        new(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this));

    private void BeginOrbitCapture(PointerPressedEventArgs e)
    {
        if (TryGetActiveRenderer(out var activeRenderer))
            activeRenderer.Scene!.SetArcballPivot(GetDefaultArcballPivot(activeRenderer));

        snapAnimating = false;

        if (!PointerCapture.TryBegin(this, e.Pointer, localPointer))
            return;

        rotating = true;
        orbitCaptureMode = true;
        centerFadeTarget = CenterFadeStrength;

        EnsureAnimationRunning(resetClock: true);
        InvalidateVisual();

        e.Handled = true;
    }

    private void BeginAxisPress(PointerPressedEventArgs e)
    {
        pressedAxisId = hoveredAxisId;
        pressedPointer = localPointer;
        pressedAxisWorldDirection = AxisDirections[pressedAxisId];
        orbitCaptureMode = false;

        if (PointerCapture.TryBegin(this, e.Pointer, localPointer))
            e.Handled = true;
    }

    private void CompleteAxisPress(PointerReleasedEventArgs e)
    {
        var releasePointer = e.GetPosition(this);

        localPointer = releasePointer;
        hasTrackedPointer = true;

        RecomputeHover(localPointer, startAnimations: true);

        var dx = releasePointer.X - pressedPointer.X;
        var dy = releasePointer.Y - pressedPointer.Y;
        var clickDistanceSq = dx * dx + dy * dy;

        if (clickDistanceSq <= AxisClickMoveThreshold * AxisClickMoveThreshold &&
            TryGetActiveRenderer(out var activeRenderer))
        {
            StartSnapToAxis(activeRenderer, pressedAxisWorldDirection);
        }

        pressedAxisId = -1;
        e.Handled = true;
    }

    private void TrackPointer(PointerEventArgs e)
    {
        localPointer = e.GetPosition(this);
        hasTrackedPointer = true;
    }

    private void OnAnimationTick()
    {
        if (VisualRoot is null)
        {
            animationTimer.Stop();
            return;
        }

        if (!PointerCapture.IsOwnedBy(this) && hasTrackedPointer)
            RecomputeHover(localPointer, startAnimations: false);

        var changed = AdvanceAnimations(GetTickDeltaSeconds());

        if (changed || rotating || PointerCapture.IsOwnedBy(this))
            InvalidateVisual();

        if (!NeedsAnimation())
            animationTimer.Stop();
    }

    private bool NeedsAnimation()
    {
        if (rotating || PointerCapture.IsOwnedBy(this) || snapAnimating)
            return true;

        if (Math.Abs(centerFadeCurrent - centerFadeTarget) > AnimationEpsilon)
            return true;

        for (var i = 0; i < axisHoverFade.Length; i++)
        {
            var target = hoveredAxisId == i ? 1f : 0f;
            if (Math.Abs(axisHoverFade[i] - target) > AnimationEpsilon)
                return true;
        }

        for (var i = 1; i < negativeLabelFade.Length; i += 2)
        {
            var target = hoveredAxisId == i ? 1f : 0f;
            if (Math.Abs(negativeLabelFade[i] - target) > AnimationEpsilon)
                return true;
        }

        return false;
    }

    private void RecomputeHover(Point pointerPosition, bool startAnimations)
    {
        if (!TryGetActiveRenderer(out var activeRenderer))
            return;

        var scene = activeRenderer.Scene!;
        BuildHandles(scene.GetCameraViewSnapshot().Rotation, GetCenter(), GizmoAxisLineLength, handles, drawOrder);

        var previousHoveredAxis = hoveredAxisId;
        var previousHoveredCenter = hoveredCenter;

        hoveredAxisId = -1;
        hoveredCenter = IsInsideCenter(pointerPosition);

        var axisRadiusSq = GizmoCircleRadius * GizmoCircleRadius;
        foreach (var handle in handles)
        {
            var dx = pointerPosition.X - handle.ScreenPosition.X;
            var dy = pointerPosition.Y - handle.ScreenPosition.Y;

            if (dx * dx + dy * dy <= axisRadiusSq)
                hoveredAxisId = handle.Id;
        }

        centerFadeTarget = hoveredCenter || rotating ? CenterFadeStrength : 0f;

        var hoverChanged = previousHoveredAxis != hoveredAxisId || previousHoveredCenter != hoveredCenter;
        if (startAnimations && (hoverChanged || NeedsAnimation()))
            EnsureAnimationRunning();

        if (hoverChanged)
            InvalidateVisual();
    }

    private static bool IsInsideCenter(Point pointerPosition)
    {
        var center = GetCenter();
        var dx = pointerPosition.X - center.X;
        var dy = pointerPosition.Y - center.Y;

        return dx * dx + dy * dy <= GizmoBigCircleRadius * GizmoBigCircleRadius;
    }

    private void ClearHoverState()
    {
        hoveredAxisId = -1;
        hoveredCenter = false;
        centerFadeTarget = 0f;

        EnsureAnimationRunning();
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
        return AnimateValue(ref centerFadeCurrent, centerFadeTarget, CenterFadeRate, deltaSeconds);
    }

    private bool AnimateNegativeLabels(float deltaSeconds)
    {
        var changed = false;

        for (var i = 1; i < negativeLabelFade.Length; i += 2)
        {
            var target = hoveredAxisId == i ? 1f : 0f;
            changed |= AnimateValue(ref negativeLabelFade[i], target, NegativeLabelFadeRate, deltaSeconds);
        }

        return changed;
    }

    private bool AnimateAxisHover(float deltaSeconds)
    {
        var changed = false;

        for (var i = 0; i < axisHoverFade.Length; i++)
        {
            var target = hoveredAxisId == i ? 1f : 0f;
            changed |= AnimateValue(ref axisHoverFade[i], target, HoverFadeRate, deltaSeconds);
        }

        return changed;
    }

    private static bool AnimateValue(ref float value, float target, float rate, float deltaSeconds)
    {
        var next = StepToward(value, target, rate, deltaSeconds);

        if (Math.Abs(next - value) <= AnimationEpsilon)
        {
            var changed = Math.Abs(value - target) > AnimationEpsilon;
            value = target;
            return changed;
        }

        value = next;
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

        scene.SetCameraView(
            Vector3.Lerp(snapStartPosition, snapTargetPosition, eased),
            Quaternion.Slerp(snapStartRotation, snapTargetRotation, eased));

        if (t >= 1f)
            snapAnimating = false;

        return true;
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
        if (axisDirectionFromPivot.LengthSquared() < 0.000001f || activeViewer.Scene is null)
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
        currentDirection = currentDirection.LengthSquared() < 0.000001f
            ? -targetDirection
            : Vector3.Normalize(currentDirection);

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

        if (resolved?.Scene is { } scene)
        {
            if (!ReferenceEquals(viewer, resolved))
                viewer = resolved;

            AttachObservedScene(scene);
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
            if (VisualRoot is null)
                return;

            if (IsPointerOver && !PointerCapture.IsOwnedBy(this))
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
        if (VisualRoot is null || PointerCapture.IsOwnedBy(this))
            return;

        localPointer = e.GetPosition(this);
        hasTrackedPointer = true;
    }

    private static Point GetCenter() => new(GizmoBigCircleRadius, GizmoBigCircleRadius);

    private static void BuildHandles(
        Quaternion cameraRotation,
        Point center,
        double axisLength,
        AxisHandle[] targetHandles,
        int[] targetDrawOrder)
    {
        var inverse = Quaternion.Inverse(cameraRotation);

        for (var i = 0; i < AxisDirections.Length; i++)
        {
            var axisIndex = i / 2;
            var viewDir = Vector3.Transform(AxisDirections[i], inverse);

            targetHandles[i] = new AxisHandle(
                i,
                axisIndex,
                viewDir.Z,
                new Point(center.X - viewDir.X * axisLength, center.Y - viewDir.Y * axisLength));

            targetDrawOrder[i] = i;
        }

        Array.Sort(targetDrawOrder, (left, right) =>
            targetHandles[left].Depth.CompareTo(targetHandles[right].Depth));
    }

    private static Vector3 GetDefaultArcballPivot(VulkanViewerControl activeViewer)
    {
        var scene = activeViewer.Scene!;
        var cameraView = scene.GetCameraViewSnapshot();

        var forward = Vector3.Transform(Vector3.UnitZ, cameraView.Rotation);
        forward = forward.LengthSquared() < 0.000001f
            ? Vector3.UnitZ
            : Vector3.Normalize(forward);

        return cameraView.Position + forward * DefaultArcballPivotDistance;
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

            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        var rotationAxis = Vector3.Normalize(Vector3.Cross(fromNorm, toNorm));
        return Quaternion.CreateFromAxisAngle(rotationAxis, MathF.Acos(dot));
    }

    private static float StepToward(float value, float target, float rate, float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
            return value;

        var t = 1f - MathF.Exp(-rate * deltaSeconds);
        return value + (target - value) * t;
    }

    private static bool IsPrimaryAxisHandle(int id) => (id & 1) == 0;

    private static Color WithAlpha(Color color, float alphaScale)
    {
        var alpha = (byte)(color.A * Math.Clamp(alphaScale, 0f, 1f));
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color Darken(Color color, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);

        return Color.FromArgb(
            color.A,
            (byte)(color.R * factor),
            (byte)(color.G * factor),
            (byte)(color.B * factor));
    }

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
