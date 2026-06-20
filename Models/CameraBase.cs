using System;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Serilog;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Inspector;

namespace UniversalUmap.Rendering.Scenes;

public abstract class CameraBase : IGpuSnapshot<CameraDataGpu>, IInspectable
{
    private static readonly Vector3 WorldUp = new(0f, -1f, 0f);
    protected static readonly Vector3 LocalForward = new(0f, 0f, 1f);
    protected static readonly Vector3 LocalUp = new(0f, -1f, 0f);
    protected static readonly Vector3 LocalRight = new(1f, 0f, 0f);
    private const float FlySensitivityDegrees = 0.1f;
    private const float SpeedBoostMultiplier = 10f;
    protected const float DataEpsilon = 0.0001f;
    private const float WheelDollyScale = 0.8f;
    private const double DirectInteractionHoldSeconds = 0.15d;

    private readonly Input input;
    private CameraDataGpu data = new();
    private Vector3 position = new(0f, 0f, -2f);
    private Quaternion rotation = Quaternion.Identity;
    protected PixelSize lastRenderSize = new(1, 1);
    private double lastInputLogSeconds;
    private long directInteractionUntilTicks;

    public abstract CameraProjectionType Projection { get; }

    public string InspectorTitle => "Camera";

    internal event Action<CameraProjectionType>? ProjectionChangeRequested;

    [Detail("Projection", Group = "Camera", Order = -100)]
    public CameraProjectionType ProjectionSetting
    {
        get => Projection;
        set
        {
            if (value != Projection)
                ProjectionChangeRequested?.Invoke(value);
        }
    }

    [DetailInline]
    public CameraSettings Settings { get; }

    internal CameraBase(Input input, CameraSettings settings)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Settings.Changed += RebuildData;
        RebuildData();
    }

    public float MoveSpeed { get; private set; } = 12f;
    public Vector3 ArcballPivot { get; protected set; } = Vector3.Zero;

    [Detail("Position", Group = "Transform")]
    public Vector3 Position => position;
    public Quaternion Rotation => rotation;
    public int IsMoving { get; private set; }
    internal CameraDataGpu Data => data;
    internal event Action? Changed;

    CameraDataGpu IGpuSnapshot<CameraDataGpu>.ToStruct() => ToStruct();

    internal CameraDataGpu ToStruct() => data;

    internal void DetachSettings() => Settings.Changed -= RebuildData;

    public void SetArcballPivot(Vector3 pivot)
    {
        ArcballPivot = pivot;
        RebuildData();
    }

    public void OrbitAroundPivot(float yawRadians, float pitchRadians)
    {
        if (Math.Abs(yawRadians) < 0.000001f && Math.Abs(pitchRadians) < 0.000001f)
            return;

        var yawQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(WorldUp), yawRadians);
        var localRight = Vector3.Transform(LocalRight, rotation);
        var pitchQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(localRight), pitchRadians);
        var offset = position - ArcballPivot;
        var rotationDelta = Quaternion.Normalize(yawQuat * pitchQuat);
        offset = Vector3.Transform(offset, rotationDelta);
        position = ArcballPivot + offset;
        rotation = Quaternion.Normalize(yawQuat * pitchQuat * rotation);
        MarkDirectInteraction();
        RebuildData();
    }

    public void PanInViewPlane(float deltaX, float deltaY)
    {
        if (Math.Abs(deltaX) < 0.000001f && Math.Abs(deltaY) < 0.000001f)
            return;

        var right = Vector3.Normalize(Vector3.Transform(LocalRight, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));
        var distance = Math.Max(1f, Vector3.Distance(position, ArcballPivot));
        var sensitivity = 0.0015f * distance;
        position += right * (-deltaX * sensitivity) + up * (deltaY * sensitivity);
        MarkDirectInteraction();
        RebuildData();
    }

    public virtual void Dolly(float amount)
    {
        if (Math.Abs(amount) < 0.000001f)
            return;

        var forward = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var distance = Math.Max(1f, Vector3.Distance(position, ArcballPivot));
        position += forward * (amount * Math.Max(0.25f, distance * 0.05f));
        MarkDirectInteraction();
        RebuildData();
    }

    public void SetPosition(Vector3 value)
    {
        if (Vector3.DistanceSquared(position, value) <= DataEpsilon * DataEpsilon)
            return;

        position = value;
        MarkDirectInteraction();
        RebuildData();
    }

    public void SetRotation(Quaternion value)
    {
        var normalizedValue = Quaternion.Normalize(value);
        var delta = Quaternion.Dot(rotation, normalizedValue);
        if (MathF.Abs(MathF.Abs(delta) - 1f) <= DataEpsilon)
            return;

        rotation = normalizedValue;
        MarkDirectInteraction();
        RebuildData();
    }

    public void FocusOnWorldPosition(Vector3 worldPosition)
    {
        ArcballPivot = worldPosition;
        Settings.SetFocusDistanceSilent(Vector3.Distance(position, worldPosition));
        MarkDirectInteraction();
        RebuildData();
    }

    public virtual void FrameBoundingBox(Vector3 center, float radius)
    {
        var forward = Vector3.Transform(LocalForward, rotation);
        position = center - forward * (radius * 3f);
        ArcballPivot = center;
        Settings.SetFocusDistanceSilent(Vector3.Distance(Position, center));
        MarkDirectInteraction();
        RebuildData();
    }

    public void Update(PixelSize renderSize, float deltaTimeSeconds)
    {
        lastRenderSize = renderSize;

        var frameInput = input.ConsumeFrameSnapshot();
        var wheelDelta = frameInput.WheelDelta;
        var mouseDelta = frameInput.MouseDelta;
        var leftMouseDown = frameInput.LeftMouseDown;
        var rightMouseDown = frameInput.RightMouseDown;
        var dragActive = leftMouseDown || rightMouseDown;
        var hasMovementKeys = frameInput.MoveForward || frameInput.MoveLeft || frameInput.MoveBackward ||
                              frameInput.MoveRight || frameInput.MoveDown || frameInput.MoveUp;
        var moving = false;

        if (Math.Abs(wheelDelta) > 0.0001f)
        {
            Dolly(wheelDelta * WheelDollyScale);
            moving = true;
        }

        if (dragActive)
            moving |= UpdateFly(mouseDelta, deltaTimeSeconds, frameInput);

        if ((Math.Abs(wheelDelta) > 0.0001f || mouseDelta.LengthSquared() > 0.0001f || hasMovementKeys || dragActive) &&
            (System.Environment.TickCount64 / 1000.0 - lastInputLogSeconds) > 0.25)
        {
            lastInputLogSeconds = System.Environment.TickCount64 / 1000.0;
            Log.Debug(
                "Camera input frame: LMB={Lmb} RMB={Rmb} Fly={Fly} mouseDelta=({Dx:0.00},{Dy:0.00}) wheel={Wheel:0.00} moveKeys={MoveKeys} pos=({Px:0.00},{Py:0.00},{Pz:0.00})",
                leftMouseDown,
                rightMouseDown,
                dragActive,
                mouseDelta.X,
                mouseDelta.Y,
                wheelDelta,
                hasMovementKeys,
                position.X,
                position.Y,
                position.Z);
        }

        if (!moving && Stopwatch.GetTimestamp() <= directInteractionUntilTicks)
            moving = true;

        IsMoving = moving ? 1 : 0;
        UpdateData(renderSize);
    }

    public void UpdateRenderSize(PixelSize renderSize)
    {
        lastRenderSize = renderSize;
        UpdateData(renderSize);
    }

    internal static bool IsDataEquivalent(in CameraDataGpu a, in CameraDataGpu b)
    {
        var epsilonSq = DataEpsilon * DataEpsilon;
        return Vector3.DistanceSquared(a.Position, b.Position) <= epsilonSq &&
               Vector3.DistanceSquared(a.Direction, b.Direction) <= epsilonSq &&
               Vector3.DistanceSquared(a.Horizontal, b.Horizontal) <= epsilonSq &&
               Vector3.DistanceSquared(a.Vertical, b.Vertical) <= epsilonSq &&
               MathF.Abs(a.FocalLength - b.FocalLength) <= DataEpsilon &&
               MathF.Abs(a.FocusDistance - b.FocusDistance) <= DataEpsilon &&
               MathF.Abs(a.Aperture - b.Aperture) <= DataEpsilon &&
               MathF.Abs(a.BokehBias - b.BokehBias) <= DataEpsilon &&
               a.CameraType == b.CameraType &&
               MathF.Abs(a.OrthoHeight - b.OrthoHeight) <= DataEpsilon &&
               MathF.Abs(a.FisheyeFov - b.FisheyeFov) <= DataEpsilon &&
               MathF.Abs(a.NearPlane - b.NearPlane) <= DataEpsilon &&
               MathF.Abs(a.FarPlane - b.FarPlane) <= DataEpsilon;
    }

    protected void RebuildData() => UpdateData(lastRenderSize);

    private void UpdateData(PixelSize renderSize)
    {
        var width = Math.Max(1, renderSize.Width);
        var height = Math.Max(1, renderSize.Height);
        var aspectRatio = (float)width / height;
        var direction = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));
        var right = Vector3.Normalize(Vector3.Cross(direction, up));

        var next = data;
        next.Position = position;
        next.Direction = direction;
        next.CameraType = (int)Projection;
        next.NearPlane = 0.01f;
        next.FarPlane = 10000f;

        ComputeProjectionData(ref next, direction, up, right, width, height, aspectRatio);

        if (IsDataEquivalent(data, next))
            return;

        data = next;
        Changed?.Invoke();
    }

    protected abstract void ComputeProjectionData(
        ref CameraDataGpu next, Vector3 direction, Vector3 up, Vector3 right,
        int width, int height, float aspectRatio);

    protected void ApplyDepthOfField(ref CameraDataGpu next, float focalLengthMm)
    {
        next.FocusDistance = Settings.FocusDistance;
        next.BokehBias = Settings.BokehBias;
        next.Aperture = Settings.Aperture > 0f
            ? (focalLengthMm / Settings.Aperture) * 0.5f * 0.001f
            : 0f;
    }

    protected static void ClearDepthOfField(ref CameraDataGpu next)
    {
        next.FocusDistance = 0f;
        next.BokehBias = 0f;
        next.Aperture = 0f;
    }

    private bool UpdateFly(Vector2 mouseDeltaPixels, float deltaTimeSeconds, Input.FrameSnapshot frameInput)
    {
        var positionBefore = position;
        var rotationBefore = rotation;

        var yaw = DegreesToRadians(-mouseDeltaPixels.X * FlySensitivityDegrees);
        var pitch = DegreesToRadians(-mouseDeltaPixels.Y * FlySensitivityDegrees);
        if (Math.Abs(yaw) > 0.000001f || Math.Abs(pitch) > 0.000001f)
        {
            var yawQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(WorldUp), yaw);
            var rightDir = Vector3.Transform(LocalRight, rotation);
            var pitchQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(rightDir), pitch);
            rotation = Quaternion.Normalize(yawQuat * pitchQuat * rotation);
        }

        var speed = deltaTimeSeconds * MoveSpeed;
        if (frameInput.Boost)
            speed *= SpeedBoostMultiplier;

        var forward = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));
        var right = Vector3.Normalize(Vector3.Cross(forward, up));

        if (frameInput.MoveForward)
            position += forward * speed;
        if (frameInput.MoveBackward)
            position -= forward * speed;
        if (frameInput.MoveLeft)
            position -= right * speed;
        if (frameInput.MoveRight)
            position += right * speed;
        if (frameInput.MoveUp)
            position += up * speed;
        if (frameInput.MoveDown)
            position -= up * speed;

        return position != positionBefore || rotation != rotationBefore;
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);

    protected void MarkDirectInteraction()
    {
        directInteractionUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * DirectInteractionHoldSeconds);
        IsMoving = 1;
    }
}
